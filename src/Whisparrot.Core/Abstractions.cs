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

public sealed record InjectResult(InjectOutcome Outcome, string? Message = null);

/// <summary>前景アプリへテキストを入れる。投入確定後は取消できない。</summary>
public interface ITextInjector
{
    Task<InjectResult> InjectAsync(string text, CancellationToken ct);
}
