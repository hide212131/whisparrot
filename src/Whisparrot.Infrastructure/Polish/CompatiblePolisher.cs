using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Whisparrot.Core;
using Whisparrot.Infrastructure.Network;

namespace Whisparrot.Infrastructure.Polish;

/// <summary>
/// 設計 §7.2: <c>POST &lt;base_url&gt;/chat/completions</c>（OpenAI 互換）。固定の指示は system、認識テキストは user の別メッセージで、
/// 指示へ文字列結合しない。通信は <see cref="NetworkContext"/>（Cleanup）だけを通し、リダイレクトは追わない。
/// どの失敗でも例外ではなく <see cref="PolishResult.Fallback"/> を返す（利用者の取消だけは <see cref="OperationCanceledException"/>）。
/// 失敗の補足に応答本文・URL・例外メッセージ・キーを入れない。
/// </summary>
public sealed class CompatiblePolisher
{
    /// <summary>受信上限（UTF-8 で 1 MiB。超過は原文へ戻す）。</summary>
    public const int MaxResponseBytes = 1024 * 1024;

    private static readonly Lazy<string> InstructionsText = new(LoadInstructions);

    private readonly NetworkContext _network;
    private readonly Uri _endpoint;
    private readonly string _model;
    private readonly string? _apiKey;
    private readonly TimeSpan _timeout;

    public CompatiblePolisher(NetworkContext network, Uri baseUrl, string model, string? apiKey, TimeSpan timeout)
    {
        _network = network;
        _endpoint = ChatCompletionsUrl(baseUrl);
        _model = model;
        _apiKey = string.IsNullOrEmpty(apiKey) ? null : apiKey;
        _timeout = timeout;
    }

    /// <summary>固定の整文指示（実装リソース。利用者向けの編集機能はない）。</summary>
    public static string Instructions => InstructionsText.Value;

    /// <summary>ベース URL（API root）の末尾の <c>/</c> を正規化して <c>chat/completions</c> を付ける。<c>/v1</c> を消さない。</summary>
    public static Uri ChatCompletionsUrl(Uri baseUrl) =>
        new(baseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/chat/completions");

    /// <summary>送るのは model / messages（system + user）/ stream:false だけ。temperature 等の任意パラメーターは送らない。</summary>
    public static byte[] BuildRequestBody(string model, string rawText)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("model", model);
            w.WriteStartArray("messages");
            w.WriteStartObject();
            w.WriteString("role", "system");
            w.WriteString("content", Instructions);
            w.WriteEndObject();
            w.WriteStartObject();
            w.WriteString("role", "user");
            w.WriteString("content", rawText);
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteBoolean("stream", false);
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    /// <summary>整文を実行する。15 秒（設定値）の期限は接続・送信・受信の全体。<paramref name="ct"/> の取消だけが例外になる。</summary>
    public async Task<PolishResult> PolishAsync(string rawText, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(_timeout);
        try
        {
            using var client = _network.CreateClient(NetworkPurpose.Cleanup, timeout: Timeout.InfiniteTimeSpan);
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = new ByteArrayContent(BuildRequestBody(_model, rawText)),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            if (_apiKey is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is 401 or 403) return PolishResult.Fallback(PolishFailure.Auth, $"HTTP {status}");
            if (status is < 200 or > 299) return PolishResult.Fallback(PolishFailure.Http, $"HTTP {status}"); // 3xx も追わない

            if (response.Content.Headers.ContentLength is > MaxResponseBytes) return PolishResult.Fallback(PolishFailure.TooLarge);
            var body = await ReadLimitedAsync(await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false), deadline.Token).ConfigureAwait(false);
            return body is null ? PolishResult.Fallback(PolishFailure.TooLarge) : ParseResponse(body);
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested) throw; // 利用者の取消。失敗として扱わない
            return PolishResult.Fallback(PolishFailure.Timeout);
        }
        catch (HttpRequestException)
        {
            return PolishResult.Fallback(PolishFailure.Network);
        }
        catch (Exception ex) when (ex is NetworkConfigurationException or NetworkPolicyException)
        {
            return PolishResult.Fallback(PolishFailure.Network);
        }
        catch
        {
            return PolishResult.Fallback(PolishFailure.Error);
        }
    }

    private static async Task<byte[]?> ReadLimitedAsync(Stream stream, CancellationToken ct)
    {
        await using var _ = stream.ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int n;
        while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + n > MaxResponseBytes) return null;
            ms.Write(buffer, 0, n);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// <c>choices[0].message.content</c> の文字列と、正常な終了（<c>finish_reason == "stop"</c>）を確認する。
    /// 途中打ち切り・ツール呼出し・非文字列・空白だけ・JSON でない応答は成功扱いしない。複数候補は連結しない（先頭だけ見る）。
    /// </summary>
    public static PolishResult ParseResponse(ReadOnlySpan<byte> body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body.ToArray());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0
                || choices[0].ValueKind != JsonValueKind.Object)
                return PolishResult.Fallback(PolishFailure.Malformed);
            var choice = choices[0];

            if (!choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                return PolishResult.Fallback(PolishFailure.Malformed);
            if (HasNonEmpty(message, "tool_calls") || HasNonEmpty(message, "function_call"))
                return PolishResult.Fallback(PolishFailure.Malformed);

            var finished = choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String && fr.GetString() == "stop";
            if (!finished) return PolishResult.Fallback(PolishFailure.Incomplete);

            if (!message.TryGetProperty("content", out var content) || content.ValueKind == JsonValueKind.Null)
                return PolishResult.Fallback(PolishFailure.Empty);
            if (content.ValueKind != JsonValueKind.String) return PolishResult.Fallback(PolishFailure.Malformed);
            var text = content.GetString()!.Trim();
            return text.Length == 0 ? PolishResult.Fallback(PolishFailure.Empty) : PolishResult.Success(text);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return PolishResult.Fallback(PolishFailure.Malformed);
        }
    }

    private static bool HasNonEmpty(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
        && !(v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 0);

    private static string LoadInstructions()
    {
        using var s = typeof(CompatiblePolisher).Assembly.GetManifestResourceStream("cleanup-instructions.txt")
            ?? throw new InvalidOperationException("cleanup-instructions.txt が埋め込まれていません");
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd().Replace("\r\n", "\n").TrimEnd('\n');
    }
}
