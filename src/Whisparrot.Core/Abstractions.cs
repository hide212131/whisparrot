namespace Whisparrot.Core;

/// <summary>16 kHz mono float の PCM。録音停止時に固定され、認識の再試行が終わるまで親が保持する。</summary>
public sealed record PcmAudio(float[] Samples)
{
    public const int SampleRate = 16000;
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Samples.Length / SampleRate);
}

/// <summary>録音。開始から停止または取消までデバイスを占有し、どの終了経路でも解放する。</summary>
public interface IRecorder : IDisposable
{
    /// <summary>入力レベル（0..1、約 50 ms ごと）。任意のスレッドから呼ばれる。</summary>
    event Action<float>? LevelChanged;

    /// <summary>録音中に致命的なデバイスエラー（切断など）が起きた。デバイスは解放済み。</summary>
    event Action<RecorderError>? Failed;

    /// <summary>録音を開始する。デバイスが使えなければ <see cref="RecorderException"/>。</summary>
    void Start();

    /// <summary>停止して末尾の余韻まで回収し、PCM を返す。</summary>
    Task<PcmAudio> StopAsync();

    /// <summary>停止して PCM を捨て、デバイスを解放する。</summary>
    void Cancel();
}

public enum RecorderErrorKind { AccessDenied, DeviceUnavailable, Disconnected, Other }

public sealed record RecorderError(RecorderErrorKind Kind, string Message);

public sealed class RecorderException(RecorderErrorKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public RecorderErrorKind Kind { get; } = kind;
}

public sealed record RecognitionRequest(PcmAudio Pcm, string Language = "ja");

/// <param name="Backend">ワーカーが報告した実際の実行方式（例: "cpu"）。LoadedLibrary だけでなくログ照合を含む。</param>
public sealed record RecognitionResult(string Text, string Backend);

public sealed class RecognitionException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>ローカル認識。実装は子プロセスで隔離する。</summary>
public interface IRecognizer : IDisposable
{
    /// <summary>認識できる状態でなければ理由（利用者向け）を返す。準備済みなら null。</summary>
    string? CheckReady();

    /// <summary>認識する。取消は <paramref name="ct"/>。失敗は <see cref="RecognitionException"/>。</summary>
    Task<RecognitionResult> RecognizeAsync(RecognitionRequest request, CancellationToken ct);
}

public enum InjectOutcome { Success, Failed }

/// <summary>Route は実際に使った貼り付け経路（paste-message / send-input）、TargetClass は投入直前に解決した宛先のウィンドウクラス。持たない実装・失敗では null。</summary>
/// <param name="EffectUnknown">失敗だが、送出の可能性がありテキストが入ったか確認できない（ストリーミングの厳密な投入だけが立てる）。</param>
public sealed record InjectResult(InjectOutcome Outcome, string? Message = null, string? Route = null, string? TargetClass = null, bool EffectUnknown = false);

/// <summary>録音開始時に記録した投入先（ウィンドウとフォーカス中コントロール）。中身は実装だけが解釈する。</summary>
public interface IInjectTarget { }

/// <summary>前景アプリへテキストを入れる。投入確定後は取消できない。</summary>
public interface ITextInjector
{
    /// <summary>投入時点の前景ウィンドウへ入れる（従来動作）。</summary>
    Task<InjectResult> InjectAsync(string text, CancellationToken ct);

    /// <summary>録音開始時に呼ぶ。投入先として使える前景ウィンドウを記録して返す。記録できない（自プロセス・シェル・デスクトップ等）ときは null。</summary>
    IInjectTarget? CaptureTarget() => null;

    /// <summary>記録した投入先へ入れる。<paramref name="target"/> が null なら <see cref="InjectAsync(string, CancellationToken)"/> と同じ（投入時点の前景）。</summary>
    Task<InjectResult> InjectAsync(string text, IInjectTarget? target, CancellationToken ct) => InjectAsync(text, ct);

    /// <summary>
    /// ストリーミングの区間を入れる（設計 §4.2）。記録した投入先を厳密に確認し、前景・フォーカス・宛先が記録と違えば何も送らず失敗する
    /// （代替コントロールへの再解決・別の前景への投入・記録した窓の自動前景化をしない）。送出の可能性がある時点からは別経路へ落とさない。
    /// <paramref name="ct"/> は副作用（クリップボード書き込み・送出）の直前まで有効。実装しない注入先は従来の投入と同じ。
    /// </summary>
    Task<InjectResult> InjectSegmentAsync(string text, IInjectTarget target, CancellationToken ct) => InjectAsync(text, target, ct);
}
