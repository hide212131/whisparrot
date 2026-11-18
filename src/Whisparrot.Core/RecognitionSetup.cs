using Whisparrot.Core.Settings;

namespace Whisparrot.Core;

/// <summary>
/// 1 セッション分の認識の構え（クラウド文字起こし設計 §3.1）。録音開始時に確定し、途中の設定変更は次のセッションから効く。
/// <see cref="PolishSetup"/> と同じ流儀。
/// </summary>
public abstract record RecognitionSetup
{
    /// <summary>認識を始められない。録音せず、固定の理由（秘密・設定値を含まない）を利用者へ知らせる。</summary>
    public sealed record Unconfigured(string Reason) : RecognitionSetup;

    /// <summary>
    /// 認識を実行できる。<paramref name="Deadline"/> は認識開始から結果までの全体の期限で、null なら Coordinator は期限を設けない（ローカル）。
    /// 失敗は <see cref="RecognitionException"/>（メッセージは利用者向けの固定文言）、取消は <see cref="OperationCanceledException"/>。
    /// </summary>
    public sealed record Ready(
        RecognitionEngine Engine,
        Func<RecognitionRequest, CancellationToken, Task<RecognitionResult>> RecognizeAsync,
        TimeSpan? Deadline) : RecognitionSetup;
}

/// <summary>現在の設定から認識の構えを作る。例外を投げない（投げた場合は Coordinator が Unconfigured 扱いにする）。</summary>
public interface IRecognitionSource
{
    RecognitionSetup Prepare();
}

/// <summary>既存の単一認識（ローカル）をそのまま構えにする。<see cref="IRecognizer.CheckReady"/> と認識呼出しへ委譲し、追加の期限は設けない。</summary>
public sealed class FixedRecognitionSource(IRecognizer recognizer) : IRecognitionSource
{
    public RecognitionSetup Prepare() =>
        recognizer.CheckReady() is { } problem
            ? new RecognitionSetup.Unconfigured(problem)
            : new RecognitionSetup.Ready(RecognitionEngine.Local, recognizer.RecognizeAsync, null);
}
