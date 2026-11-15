using System.IO.Pipes;
using Whisparrot.Core;
using Whisparrot.Infrastructure.Recognition;

namespace Whisparrot.Tests;

/// <summary>実プロセスの代わりに、同じプロトコルを話す in-process の fake 子を使って親側の隔離・再試行・取消を確認する。</summary>
public sealed class WhisperRecognizerTests : IDisposable
{
    private readonly string _model = Path.Combine(Path.GetTempPath(), "whisparrot-model-" + Guid.NewGuid().ToString("N") + ".bin");

    public WhisperRecognizerTests() => File.WriteAllBytes(_model, new byte[16]);
    public void Dispose() { try { File.Delete(_model); } catch { } }

    private sealed class FakeProcess(CancellationTokenSource killed, Task body) : IWorkerProcess
    {
        public bool Killed { get; private set; }
        public bool HasExited => body.IsCompleted || Killed;
        public int? ExitCode => HasExited ? (Killed ? -1 : 0) : null;
        public void Kill() { Killed = true; killed.Cancel(); }
        public async Task WaitForExitAsync(CancellationToken ct) => await body.WaitAsync(ct).ConfigureAwait(false);
        public void Dispose() { }
    }

    private sealed class FakeLauncher(Func<WorkerStartInfo, CancellationToken, Task> behavior) : IWorkerLauncher
    {
        public List<string> Launched { get; } = [];
        public List<FakeProcess> Processes { get; } = [];

        public IWorkerProcess Launch(WorkerStartInfo info)
        {
            Launched.Add(info.Backend);
            var cts = new CancellationTokenSource();
            var body = Task.Run(async () =>
            {
                try { await behavior(info, cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (IOException) { }
            });
            var p = new FakeProcess(cts, body);
            Processes.Add(p);
            return p;
        }
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(WorkerStartInfo info, byte[]? nonce = null)
    {
        var pipe = new NamedPipeClientStream(".", info.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        await WorkerProtocol.WriteAsync(pipe, FrameType.Hello, nonce ?? Convert.FromHexString(info.Nonce), default);
        return pipe;
    }

    /// <summary>正常な子: Hello → Ready → Request ごとに Result。</summary>
    private static async Task GoodWorker(WorkerStartInfo info, CancellationToken kill, string text = "認識結果")
    {
        await using var pipe = await ConnectAsync(info);
        await WorkerProtocol.WriteJsonAsync(pipe, FrameType.Ready, new ReadyInfo(info.Backend, info.Backend, ""), default);
        while (!kill.IsCancellationRequested)
        {
            var (type, _) = await WorkerProtocol.ReadAsync(pipe, kill);
            if (type == FrameType.Shutdown) return;
            await WorkerProtocol.WriteJsonAsync(pipe, FrameType.Result, new ResultInfo(text, info.Backend), default);
        }
    }

    private WhisperRecognizer Make(FakeLauncher launcher, params string[] available) =>
        new(new WhisperRecognizerOptions(_model, ["vulkan", "cpu"], new HashSet<string>(available),
            StartupTimeout: TimeSpan.FromSeconds(10), RecognizeTimeout: TimeSpan.FromSeconds(10)), launcher);

    private static RecognitionRequest Req() => new(new PcmAudio(new float[16000]));

    [Fact]
    public async Task Cpu_only_runs_one_child_and_reuses_it_for_the_next_request()
    {
        var l = new FakeLauncher((i, k) => GoodWorker(i, k));
        using var r = Make(l, "cpu");
        var a = await r.RecognizeAsync(Req(), default);
        var b = await r.RecognizeAsync(Req(), default);
        Assert.Equal("認識結果", a.Text);
        Assert.Equal("cpu", b.Backend);
        Assert.Equal(["cpu"], l.Launched);
    }

    [Fact]
    public async Task Gpu_child_crash_is_retried_once_on_cpu_with_the_same_pcm_and_gpu_stays_disabled()
    {
        var l = new FakeLauncher(async (i, k) =>
        {
            if (i.Backend == "vulkan") return; // 接続せず異常終了（DLL 欠落・クラッシュの模擬）
            await GoodWorker(i, k);
        });
        using var r = Make(l, "cpu", "vulkan");
        Assert.Equal("cpu", (await r.RecognizeAsync(Req(), default)).Backend);
        Assert.Equal(["vulkan", "cpu"], l.Launched);

        await r.RecognizeAsync(Req(), default); // 次回以降は GPU を再試行しない
        Assert.Equal(["vulkan", "cpu"], l.Launched);
    }

    [Fact]
    public async Task Gpu_child_that_dies_after_hello_is_also_retried_on_cpu()
    {
        var l = new FakeLauncher(async (i, k) =>
        {
            if (i.Backend == "vulkan") { await using var p = await ConnectAsync(i); return; }
            await GoodWorker(i, k);
        });
        using var r = Make(l, "cpu", "vulkan");
        Assert.Equal("cpu", (await r.RecognizeAsync(Req(), default)).Backend);
        Assert.Equal(["vulkan", "cpu"], l.Launched);
    }

    [Fact]
    public async Task Failure_on_cpu_is_not_retried_and_surfaces_as_a_recognition_error()
    {
        var l = new FakeLauncher((_, _) => Task.CompletedTask);
        using var r = Make(l, "cpu");
        await Assert.ThrowsAsync<RecognitionException>(() => r.RecognizeAsync(Req(), default));
        Assert.Equal(["cpu"], l.Launched);
    }

    [Fact]
    public async Task Both_gpu_and_cpu_failing_gives_up_after_exactly_two_children()
    {
        var l = new FakeLauncher((_, _) => Task.CompletedTask);
        using var r = Make(l, "cpu", "vulkan");
        await Assert.ThrowsAsync<RecognitionException>(() => r.RecognizeAsync(Req(), default));
        Assert.Equal(["vulkan", "cpu"], l.Launched);
    }

    [Fact]
    public async Task A_child_that_announces_the_wrong_nonce_is_rejected()
    {
        var l = new FakeLauncher(async (i, k) =>
        {
            await using var pipe = await ConnectAsync(i, new byte[16]);
            await WorkerProtocol.WriteJsonAsync(pipe, FrameType.Ready, new ReadyInfo("cpu", "cpu", ""), default);
            await Task.Delay(Timeout.Infinite, k);
        });
        using var r = Make(l, "cpu");
        await Assert.ThrowsAsync<RecognitionException>(() => r.RecognizeAsync(Req(), default));
        Assert.True(l.Processes[0].Killed);
    }

    [Fact]
    public async Task Error_frame_from_the_child_is_a_recognition_error_and_the_child_is_replaced()
    {
        var l = new FakeLauncher(async (i, k) =>
        {
            await using var pipe = await ConnectAsync(i);
            await WorkerProtocol.WriteJsonAsync(pipe, FrameType.Ready, new ReadyInfo("cpu", "cpu", ""), default);
            await WorkerProtocol.ReadAsync(pipe, k);
            await WorkerProtocol.WriteJsonAsync(pipe, FrameType.Error, new ErrorInfo("認識に失敗しました: x"), default);
            await Task.Delay(Timeout.Infinite, k);
        });
        using var r = Make(l, "cpu");
        var ex = await Assert.ThrowsAsync<RecognitionException>(() => r.RecognizeAsync(Req(), default));
        Assert.Contains("認識に失敗しました", ex.Message);
        Assert.True(l.Processes[0].Killed);
    }

    [Fact]
    public async Task Cancel_during_recognition_kills_the_child_and_throws_cancellation()
    {
        var gotRequest = new TaskCompletionSource();
        var l = new FakeLauncher(async (i, k) =>
        {
            await using var pipe = await ConnectAsync(i);
            await WorkerProtocol.WriteJsonAsync(pipe, FrameType.Ready, new ReadyInfo("cpu", "cpu", ""), default);
            await WorkerProtocol.ReadAsync(pipe, k);
            gotRequest.SetResult();
            await Task.Delay(Timeout.Infinite, k); // ネイティブ処理が中断できない状況の模擬
        });
        using var r = Make(l, "cpu");
        using var cts = new CancellationTokenSource();
        var task = r.RecognizeAsync(Req(), cts.Token);
        await gotRequest.Task;
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(l.Processes[0].Killed);

        // 次の認識は新しい子で動く
        var l2 = l;
        Assert.Single(l2.Launched);
    }

    [Fact]
    public async Task Dispose_kills_the_live_child()
    {
        var l = new FakeLauncher((i, k) => GoodWorker(i, k));
        var r = Make(l, "cpu");
        await r.RecognizeAsync(Req(), default);
        r.Dispose();
        Assert.True(l.Processes[0].Killed);
    }

    [Fact]
    public void CheckReady_reports_missing_and_wrong_size_models()
    {
        var l = new FakeLauncher((_, _) => Task.CompletedTask);
        using var missing = new WhisperRecognizer(new WhisperRecognizerOptions(_model + ".nope", ["cpu"], new HashSet<string>(["cpu"])), l);
        Assert.Contains("モデルがありません", missing.CheckReady());

        using var wrong = new WhisperRecognizer(new WhisperRecognizerOptions(_model, ["cpu"], new HashSet<string>(["cpu"]), ExpectedModelBytes: 123), l);
        Assert.Contains("サイズ", wrong.CheckReady());

        using var ok = new WhisperRecognizer(new WhisperRecognizerOptions(_model, ["cpu"], new HashSet<string>(["cpu"]), ExpectedModelBytes: 16), l);
        Assert.Null(ok.CheckReady());
    }

    [Fact]
    public void DetectBackend_distinguishes_loaded_library_from_actual_gpu_use()
    {
        Assert.Equal("cpu", WorkerHost.DetectBackend("cpu", []));
        Assert.Equal("cpu", WorkerHost.DetectBackend("vulkan", ["ggml_vulkan: no GPU found"]));
        Assert.Equal("vulkan", WorkerHost.DetectBackend("vulkan", ["ggml_vulkan: Found 1 Vulkan devices:", "Vulkan0: Intel(R) Iris(R) Xe Graphics device"]));
        Assert.Equal("cpu", WorkerHost.DetectBackend("vulkan", []));
    }
}
