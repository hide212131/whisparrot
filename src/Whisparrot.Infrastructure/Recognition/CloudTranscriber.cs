using System.Net.Http.Headers;
using System.Text.Json;
using Whisparrot.Core;
using Whisparrot.Core.Settings;
using Whisparrot.Infrastructure.Network;
using Whisparrot.Infrastructure.Polish;

namespace Whisparrot.Infrastructure.Recognition;

/// <summary>クラウド認識の失敗の固定分類（設計 §5.2）。利用者向け表示とテスト用で、応答本文・URL・秘密は含めない。</summary>
public enum TranscriptionFailure
{
    /// <summary>401 / 403。</summary>
    Auth,
    /// <summary>413（接続先の上限超過）。</summary>
    PayloadTooLarge,
    /// <summary>404 / 429 / 5xx / 3xx（追わない）などその他の非 2xx。</summary>
    Http,
    /// <summary>DNS / TLS / proxy / 接続断。</summary>
    Network,
    /// <summary>全体 60 秒の期限。</summary>
    Timeout,
    /// <summary>不正 JSON、text の欠損 / null / 非文字列。</summary>
    Malformed,
    /// <summary>応答が上限（1 MiB）を超えた。</summary>
    ResponseTooLarge,
    /// <summary>録音が WAV 上限（25,000,000 byte）を超える。送信 0 件。</summary>
    AudioTooLong,
    /// <summary>音声に NaN / Infinity が含まれる。送信 0 件。</summary>
    InvalidAudio,
    Error,
}

/// <summary>
/// 成功は <see cref="Text"/>（空文字列を含む。空は「認識できなかった」で、エラー分類とは区別する）、失敗は <see cref="Failure"/>。
/// <see cref="Detail"/> は安全な補足（例: "HTTP 429"）で、応答本文や例外メッセージは入れない。
/// </summary>
public sealed record TranscriptionResult
{
    private TranscriptionResult() { }

    public string? Text { get; private init; }
    public TranscriptionFailure Failure { get; private init; }
    public string? Detail { get; private init; }
    public int? HttpStatus { get; private init; }
    public bool IsSuccess => Text is not null;

    public static TranscriptionResult Success(string text) => new() { Text = text };

    public static TranscriptionResult Fail(TranscriptionFailure failure, int? httpStatus = null) =>
        new() { Failure = failure, HttpStatus = httpStatus, Detail = httpStatus is { } s ? $"HTTP {s}" : null };

    /// <summary>失敗の利用者向け固定文言（設計 §5.2）。</summary>
    public string Message => IsSuccess ? "" : Failure switch
    {
        TranscriptionFailure.Auth => "文字起こしできませんでした（認証エラー）。設定を確認してください",
        TranscriptionFailure.PayloadTooLarge => "音声が接続先の上限を超えています",
        TranscriptionFailure.Http => $"文字起こしできませんでした（{Detail ?? "サーバーエラー"}）",
        TranscriptionFailure.Network => "文字起こしできませんでした（接続エラー）",
        TranscriptionFailure.Timeout => "文字起こしできませんでした（時間切れ）",
        TranscriptionFailure.Malformed or TranscriptionFailure.ResponseTooLarge => "文字起こしの応答を解釈できませんでした",
        TranscriptionFailure.AudioTooLong => "録音が長すぎるため文字起こしできません（上限 約13分）",
        TranscriptionFailure.InvalidAudio => "音声データが不正なため文字起こしできませんでした",
        _ => "文字起こしできませんでした",
    };
}

/// <summary>
/// 設計 §7: <c>POST &lt;base_url&gt;/audio/transcriptions</c>（OpenAI 互換）。multipart は file（<c>speech.wav</c>、audio/wav）と model、および言語コードが空でないときだけ language。
/// 非空キーは <c>Authorization: Bearer</c> にだけ入れる。通信は <see cref="NetworkPurpose.Transcription"/> だけを通し、リダイレクトは追わず、再送もしない。
/// 上限検査・WAV 化・接続・アップロード・応答の受信と解析まで、<see cref="Deadline"/>（既定 60 秒）の一つの期限で守る。
/// どの失敗でも例外ではなく <see cref="TranscriptionResult"/> を返す（利用者の取消だけは <see cref="OperationCanceledException"/>）。
/// 音声・本文・キー・URL をログに出さない（この型はログを持たない）。
/// </summary>
public sealed class CloudTranscriber
{
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    /// <summary>応答の受信上限（UTF-8 で 1 MiB）。</summary>
    public const int MaxResponseBytes = CompatiblePolisher.MaxResponseBytes;

    private readonly NetworkContext _network;
    private readonly Uri _endpoint;
    private readonly string _model;
    private readonly string? _apiKey;
    private readonly string _language;
    private readonly TimeSpan _timeout;

    /// <param name="endpoint">POST 先そのもの（<see cref="CloudRecognitionConfig.Ready.Endpoint"/>）。</param>
    public CloudTranscriber(NetworkContext network, Uri endpoint, string model, string? apiKey, TimeSpan? timeout = null, string language = CloudRecognitionSettings.DefaultLanguage)
    {
        _language = language;
        _network = network;
        _endpoint = endpoint;
        _model = model;
        _apiKey = string.IsNullOrEmpty(apiKey) ? null : apiKey;
        _timeout = timeout ?? Deadline;
    }

    /// <summary><see cref="IRecognizer"/> 相当の形へ合わせる。失敗は固定文言の <see cref="RecognitionException"/>、空 text は空の結果（Coordinator が非投入にする）。</summary>
    public async Task<RecognitionResult> RecognizeAsync(RecognitionRequest request, CancellationToken ct)
    {
        var r = await TranscribeAsync(request.Pcm, ct).ConfigureAwait(false);
        return r.IsSuccess ? new RecognitionResult(r.Text!, "cloud") : throw new RecognitionException(r.Message);
    }

    public async Task<TranscriptionResult> TranscribeAsync(PcmAudio pcm, CancellationToken ct)
    {
        // 上限は確保・エンコード・通信の前に実サンプル数で厳密に判定する（超過は送信 0 件）。
        if (!WavEncoder.FitsLimit(pcm.Samples.Length)) return TranscriptionResult.Fail(TranscriptionFailure.AudioTooLong);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_timeout);
        try
        {
            var wav = WavEncoder.Encode(pcm.Samples, deadline.Token);
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(wav);
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(file, "file", "speech.wav");
            var model = new StringContent(_model);
            model.Headers.ContentType = null; // フィールドには Content-Type を付けない
            form.Add(model, "model");
            if (_language.Length > 0)
            {
                var language = new StringContent(_language);
                language.Headers.ContentType = null;
                form.Add(language, "language");
            }

            using var client = _network.CreateClient(NetworkPurpose.Transcription, timeout: Timeout.InfiniteTimeSpan);
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint) { Content = form };
            if (_apiKey is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is 401 or 403) return TranscriptionResult.Fail(TranscriptionFailure.Auth, status);
            if (status == 413) return TranscriptionResult.Fail(TranscriptionFailure.PayloadTooLarge, status);
            if (status is < 200 or > 299) return TranscriptionResult.Fail(TranscriptionFailure.Http, status); // 3xx も追わない

            if (response.Content.Headers.ContentLength is > MaxResponseBytes) return TranscriptionResult.Fail(TranscriptionFailure.ResponseTooLarge);
            var body = await CompatiblePolisher.ReadLimitedAsync(await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false), deadline.Token).ConfigureAwait(false);
            return body is null ? TranscriptionResult.Fail(TranscriptionFailure.ResponseTooLarge) : ParseResponse(body);
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested) throw; // 利用者の取消。失敗として扱わない
            return TranscriptionResult.Fail(TranscriptionFailure.Timeout);
        }
        catch (NonFiniteSampleException)
        {
            return TranscriptionResult.Fail(TranscriptionFailure.InvalidAudio);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or NetworkConfigurationException or NetworkPolicyException)
        {
            return TranscriptionResult.Fail(TranscriptionFailure.Network);
        }
        catch
        {
            return TranscriptionResult.Fail(TranscriptionFailure.Error);
        }
    }

    /// <summary>トップレベルの <c>text</c>（文字列）だけを結果にする。追加フィールドは無視。空白だけは空の成功として返す。</summary>
    public static TranscriptionResult ParseResponse(ReadOnlySpan<byte> body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body.ToArray());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                return TranscriptionResult.Fail(TranscriptionFailure.Malformed);
            return TranscriptionResult.Success(text.GetString()!.Trim());
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return TranscriptionResult.Fail(TranscriptionFailure.Malformed);
        }
    }
}
