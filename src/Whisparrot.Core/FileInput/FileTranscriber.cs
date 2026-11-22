using System.Diagnostics;
using Whisparrot.Core.Settings;

namespace Whisparrot.Core.FileInput;

/// <summary>終了コードに対応する結果の種別。</summary>
public enum TranscribeStatus
{
    Ok = 0,
    /// <summary>入力ファイルを読めない・非対応の形式（コード 3。このクラスの外で判定する）。</summary>
    InputError = 3,
    /// <summary>認識を始められない・失敗・空（コード 4）。</summary>
    RecognitionFailed = 4,
    /// <summary>整文を選んでいるが使えない・失敗（コード 5）。原文は得られている。</summary>
    PolishFailed = 5,
}

public enum PolishOutcome { Skipped, Ok, Failed }

/// <param name="PolishText">整文が成功したときだけ。</param>
/// <param name="Error">失敗の理由（利用者向けの固定文言。秘密・応答本文は含まない）。</param>
public sealed record TranscribeRun(
    TranscribeStatus Status,
    string? Error,
    RecognitionEngine? Engine,
    string? Backend,
    string? RawText,
    TimeSpan? RecognizeTime,
    PolishOutcome Polish,
    string? PolishText,
    TimeSpan? PolishTime);

/// <summary>
/// ファイル入力の認識→整文。ライブ録音と同じ <see cref="IRecognitionSource"/> / <see cref="ITextPolisher"/> の構えをそのまま使い、
/// 入力源（<see cref="PcmAudio"/>）と出力（戻り値）だけが違う。状態機械・投入・Hane は通さない。
/// </summary>
public static class FileTranscriber
{
    public static async Task<TranscribeRun> RunAsync(
        PcmAudio pcm, IRecognitionSource recognition, ITextPolisher? polisher, bool noPolish, CancellationToken ct)
    {
        RecognitionSetup setup;
        try { setup = recognition.Prepare(); }
        catch { setup = new RecognitionSetup.Unconfigured("認識の設定を読み込めません"); }
        if (setup is not RecognitionSetup.Ready ready)
            return Fail(TranscribeStatus.RecognitionFailed, ((RecognitionSetup.Unconfigured)setup).Reason, null, null);

        // 整文の構えも認識前に固定する（設定変更は次の実行から効く。ライブ録音と同じ）。
        var polish = noPolish || polisher is null ? new PolishSetup.Off() : PreparePolish(polisher);

        var sw = Stopwatch.StartNew();
        RecognitionResult result;
        try
        {
            result = await RecognizeAsync(ready, new RecognitionRequest(pcm), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Fail(TranscribeStatus.RecognitionFailed, "中断されました", ready.Engine, sw.Elapsed);
        }
        catch (Exception ex)
        {
            return Fail(TranscribeStatus.RecognitionFailed, ex is RecognitionException ? ex.Message : "認識に失敗しました", ready.Engine, sw.Elapsed);
        }
        var recognizeTime = sw.Elapsed;
        var raw = result.Text.Trim();
        if (raw.Length == 0)
            return Fail(TranscribeStatus.RecognitionFailed, "音声を認識できませんでした", ready.Engine, recognizeTime, result.Backend);

        var done = new TranscribeRun(TranscribeStatus.Ok, null, ready.Engine, result.Backend, raw, recognizeTime, PolishOutcome.Skipped, null, null);
        switch (polish)
        {
            case PolishSetup.Unconfigured u:
                // ライブでは原文へ倒して投入するが、ファイル入力は結果の記録が目的なので、整文できなかったことを失敗として残す。
                return done with { Status = TranscribeStatus.PolishFailed, Polish = PolishOutcome.Failed, Error = $"整文の設定が未完了です（{u.Reason}）" };
            case PolishSetup.Ready p:
                sw.Restart();
                var r = await PolishAsync(p, raw, ct).ConfigureAwait(false);
                var polishTime = sw.Elapsed;
                if (r.IsSuccess && !string.IsNullOrWhiteSpace(r.Text))
                    return done with { Polish = PolishOutcome.Ok, PolishText = r.Text.Trim(), PolishTime = polishTime };
                return done with
                {
                    Status = TranscribeStatus.PolishFailed, Polish = PolishOutcome.Failed, PolishTime = polishTime,
                    Error = $"整文できませんでした（{Describe(r)}）",
                };
            default:
                return done;
        }
    }

    private static TranscribeRun Fail(TranscribeStatus status, string error, RecognitionEngine? engine, TimeSpan? time, string? backend = null) =>
        new(status, error, engine, backend, null, time, PolishOutcome.Skipped, null, null);

    private static PolishSetup PreparePolish(ITextPolisher polisher)
    {
        try { return polisher.Prepare(); }
        catch { return new PolishSetup.Unconfigured("整文の設定を読み込めません"); }
    }

    /// <summary>認識側が期限を守らなくても期限で終える（<see cref="SessionCoordinator"/> と同じ扱い）。</summary>
    private static async Task<RecognitionResult> RecognizeAsync(RecognitionSetup.Ready ready, RecognitionRequest request, CancellationToken ct)
    {
        if (ready.Deadline is not { } deadline) return await ready.RecognizeAsync(request, ct).ConfigureAwait(false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var work = Task.Run(() => ready.RecognizeAsync(request, cts.Token), CancellationToken.None);
        _ = work.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        var winner = await Task.WhenAny(work, Task.Delay(deadline, ct)).ConfigureAwait(false);
        if (winner == work) return await work.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        cts.Cancel();
        throw new RecognitionException("文字起こしできませんでした（時間切れ）");
    }

    private static async Task<PolishResult> PolishAsync(PolishSetup.Ready ready, string raw, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var work = Task.Run(() => ready.PolishAsync(raw, cts.Token), CancellationToken.None);
            _ = work.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            var winner = await Task.WhenAny(work, Task.Delay(ready.Timeout, ct)).ConfigureAwait(false);
            if (winner == work) return await work.ConfigureAwait(false);
            cts.Cancel();
            return PolishResult.Fallback(ct.IsCancellationRequested ? PolishFailure.Error : PolishFailure.Timeout);
        }
        catch
        {
            return PolishResult.Fallback(PolishFailure.Error);
        }
    }

    public static string Describe(PolishResult r) => r.IsSuccess ? "空の応答" : r.Failure switch
    {
        PolishFailure.Timeout => "時間切れ",
        PolishFailure.Network => "接続できません",
        PolishFailure.Auth => "認証エラー",
        PolishFailure.Http => r.Detail ?? "サーバーエラー",
        PolishFailure.Empty => "空の応答",
        PolishFailure.TooLarge => "応答が大きすぎます",
        PolishFailure.Incomplete => "応答が未完了",
        _ => "応答を解釈できません",
    };
}
