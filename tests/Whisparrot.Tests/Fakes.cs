using Whisparrot.Core;

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

    public Task<InjectResult> InjectAsync(string text, CancellationToken ct)
    {
        Texts.Add(text);
        if (!Manual) return Task.FromResult(Next);
        _hold = new TaskCompletionSource<InjectResult>();
        return _hold.Task;
    }

    public void Release(InjectResult r) => _hold?.TrySetResult(r);
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
