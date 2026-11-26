namespace Whisparrot.Core;

public enum RawStatus { Ok, Silent, Failed }

public enum EndStatus { Completed, Cancelled, Failed }

/// <summary>voice/1 の理由コード（設計 §2.2）。固定値だけを使い、例外メッセージや本文を載せない。</summary>
public static class VoiceCodes
{
    public const string MicrophoneUnavailable = "microphone_unavailable";
    public const string MicrophoneLost = "microphone_lost";
    public const string Timeout = "timeout";
    public const string Overloaded = "overloaded";
    public const string RecognitionFailed = "recognition_failed";
    public const string PolishFailed = "polish_failed";
    public const string EngineFailed = "engine_failed";
}

/// <summary>
/// 区間結果の出力口（設計 P2）。<see cref="SessionCoordinator.BeginCapture"/> で渡すと、ストリーミングの区間経路は
/// 貼り付けの代わりにここへ通知する。コーディネーターのループスレッドから、次の順で呼ばれる:
/// <see cref="SessionStarted"/>（マイク開始後。開始失敗・開始前の取消では省かれる）→ <see cref="RawSegment"/>（seq=1 から連続、無音・失敗を含む）
/// と <see cref="PolishedSegment"/>（対応する原文の後なら区間順を問わない）→ <see cref="SessionEnded"/>（一度だけ。以後は何も呼ばれない）。
/// 実装は待たずに返すこと（I/O はキューへ渡す）。
/// </summary>
public interface ISegmentSink
{
    void SessionStarted();

    /// <param name="text"><see cref="RawStatus.Ok"/> のときだけ非空。</param>
    /// <param name="polishPending">Ok で、この区間の整文結果がこの後に必ず一件届く。</param>
    /// <param name="code"><see cref="RawStatus.Failed"/> のときだけ。</param>
    void RawSegment(int seq, RawStatus status, string? text, bool polishPending, string? code);

    /// <summary>整文の成功（<paramref name="text"/>）か失敗（<paramref name="code"/>）。失敗では原文のまま。</summary>
    void PolishedSegment(int seq, string? text, string? code);

    /// <param name="lastSeq">原文を送出した最終連番（なしは 0）。</param>
    void SessionEnded(EndStatus status, int lastSeq, string? code);
}
