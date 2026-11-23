using System.Threading.Channels;
using Whisparrot.Core;
using Whisparrot.Core.Settings;

namespace Whisparrot.Tests;

internal sealed class FakeRecorder : IRecorder
{
    private TaskCompletionSource<PcmAudio> _stop = new();
    public event Action<float>? LevelChanged;
    public event Action<RecorderError>? Failed;

    public bool Active { get; private set; }
    public int StartCount { get; private set; }
    public int CancelCount { get; private set; }
    public Exception? StartError { get; set; }
    public PcmAudio Pcm { get; set; } = new(new float[16000]);
    /// <summary>true なら StopAsync は <see cref="CompleteStop"/> まで返らない。</summary>
    public bool HoldStop { get; set; }

    public void Start()
    {
        if (StartError is not null) throw StartError;
        StartCount++;
        Active = true;
        _stop = new TaskCompletionSource<PcmAudio>();
    }

    public Task<PcmAudio> StopAsync()
    {
        Active = false; // 停止後はデバイスを解放済み
        if (!HoldStop) _stop.TrySetResult(Pcm);
        return _stop.Task;
    }

    public void CompleteStop() => _stop.TrySetResult(Pcm);

    public void Cancel()
    {
        Active = false;
        CancelCount++;
    }

    public void RaiseFailed(RecorderError e) => Failed?.Invoke(e);
    public void RaiseLevel(float l) => LevelChanged?.Invoke(l);
    public void Dispose() { }
}

internal sealed class FakeRecognizer : IRecognizer
{
    private TaskCompletionSource<RecognitionResult> _tcs = new();
    public string? NotReady { get; set; }
    public bool Manual { get; set; }
    public RecognitionResult Result { get; set; } = new("こんにちは", "cpu");
    public Exception? Error { get; set; }
    public int Calls { get; private set; }
    public CancellationToken LastToken { get; private set; }

    public string? CheckReady() => NotReady;

    public Task<RecognitionResult> RecognizeAsync(RecognitionRequest request, CancellationToken ct)
    {
        Calls++;
        LastToken = ct;
        if (Error is not null) return Task.FromException<RecognitionResult>(Error);
        if (!Manual) return Task.FromResult(Result);
        _tcs = new TaskCompletionSource<RecognitionResult>();
        ct.Register(() => _tcs.TrySetCanceled(ct));
        return _tcs.Task;
    }

    /// <summary>取消後に「遅れて」完了する認識を模す（キャンセルを無視して完了）。</summary>
    public void CompleteLate(RecognitionResult r) => _tcs.TrySetResult(r);

    public void Complete() => _tcs.TrySetResult(Result);
    public void Dispose() { }
}

internal sealed class FakeInjector : ITextInjector
{
    private TaskCompletionSource<InjectResult>? _hold;
    public List<string> Texts { get; } = [];
    public InjectResult Next { get; set; } = new(InjectOutcome.Success);
    public bool Manual { get; set; }

    /// <summary>CaptureTarget が返す投入先（録音開始時に呼ばれる）。</summary>
    public IInjectTarget? NextTarget { get; set; }
    public bool CaptureThrows { get; set; }
    public int Captures { get; private set; }
    public List<IInjectTarget?> TargetsSeen { get; } = [];

    public IInjectTarget? CaptureTarget()
    {
        Captures++;
        if (CaptureThrows) throw new InvalidOperationException("capture");
        return NextTarget;
    }

    public Task<InjectResult> InjectAsync(string text, IInjectTarget? target, CancellationToken ct)
    {
        TargetsSeen.Add(target);
        return InjectAsync(text, ct);
    }

    public Task<InjectResult> InjectAsync(string text, CancellationToken ct)
    {
        Texts.Add(text);
        if (!Manual) return Task.FromResult(Next);
        _hold = new TaskCompletionSource<InjectResult>();
        return _hold.Task;
    }

    public void Release(InjectResult r) => _hold?.TrySetResult(r);

    // ストリーミングの区間投入（InjectSegmentAsync）。呼ばれた順の本文と、渡された取消トークンを記録する。
    private readonly object _segLock = new();
    private readonly List<string> _segments = [];
    private readonly List<CancellationToken> _segTokens = [];
    public Func<string, CancellationToken, Task<InjectResult>>? SegmentHandler { get; set; }
    public IReadOnlyList<string> Segments { get { lock (_segLock) return _segments.ToArray(); } }
    public IReadOnlyList<CancellationToken> SegmentTokens { get { lock (_segLock) return _segTokens.ToArray(); } }

    public Task<InjectResult> InjectSegmentAsync(string text, IInjectTarget target, CancellationToken ct)
    {
        lock (_segLock) { _segments.Add(text); _segTokens.Add(ct); }
        return SegmentHandler?.Invoke(text, ct) ?? Task.FromResult(Next);
    }
}

/// <summary>連続録音のフェイク。<see cref="Push"/> で 16 kHz のフレームを流し、停止・取消でチャンネルを完了する。</summary>
internal sealed class FakeStreamingRecorder : IStreamingRecorder
{
    private Channel<PcmFrame> _ch = Channel.CreateUnbounded<PcmFrame>();
    private long _pos;
    public event Action<float>? LevelChanged;
    public event Action<RecorderError>? Failed;

    public bool Active { get; private set; }
    public int StartCount { get; private set; }
    public int StreamStarts { get; private set; }
    public int StopCount { get; private set; }
    public int CancelCount { get; private set; }
    public long DroppedSamples => 0;
    public Exception? StartError { get; set; }

    public void Start()
    {
        if (StartError is not null) throw StartError;
        StartCount++;
        Active = true;
    }

    public ChannelReader<PcmFrame> StartStreaming()
    {
        if (StartError is not null) throw StartError;
        StreamStarts++;
        Active = true;
        _pos = 0;
        _ch = Channel.CreateUnbounded<PcmFrame>();
        return _ch.Reader;
    }

    /// <summary>全サンプルが <paramref name="value"/> のフレームを流す（区間の識別に使う）。</summary>
    public void Push(float value, int samples)
    {
        var data = new float[samples];
        Array.Fill(data, value);
        _ch.Writer.TryWrite(new PcmFrame(_pos, data));
        _pos += samples;
    }

    public void PushSeconds(float value, double seconds) => Push(value, (int)(seconds * PcmAudio.SampleRate));

    /// <summary>音声の欠け（受け入れを省いた箇所）を作る。</summary>
    public void Skip(int samples) => _pos += samples;

    public Task StopStreamingAsync()
    {
        Active = false;
        StopCount++;
        _ch.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public Task<PcmAudio> StopAsync()
    {
        Active = false;
        return Task.FromResult(new PcmAudio(new float[16000]));
    }

    public void Cancel()
    {
        Active = false;
        CancelCount++;
        _ch.Writer.TryComplete();
    }

    public void RaiseFailed(RecorderError e) => Failed?.Invoke(e);
    public void Dispose() { }
}

internal sealed class FakeRecognitionSource(
    Func<RecognitionRequest, CancellationToken, Task<RecognitionResult>> handler, RecognitionEngine engine = RecognitionEngine.Cloud) : IRecognitionSource
{
    public RecognitionSetup Prepare() => new RecognitionSetup.Ready(engine, handler, null);
}

internal sealed class FakePolisher(Func<string, CancellationToken, Task<PolishResult>> handler, TimeSpan timeout) : ITextPolisher
{
    public PolishSetup Prepare() => new PolishSetup.Ready(handler, timeout);
}

internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException($"待機がタイムアウト: {what}");
            await Task.Delay(5);
        }
    }
}

internal sealed record FakeTarget(string Name) : IInjectTarget;
