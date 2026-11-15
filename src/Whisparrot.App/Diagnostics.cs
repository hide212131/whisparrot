using System.Diagnostics;
using Whisparrot.Core;
using Whisparrot.Infrastructure;
using Whisparrot.Infrastructure.Audio;
using Whisparrot.Infrastructure.Recognition;

namespace Whisparrot.App;

/// <summary>
/// 非対話セッションでも実行できる診断モード。マイクや画面が無くても、本物の状態機械・認識ワーカーを通せる。
///   --native-info                      埋め込み native の展開結果を表示する
///   --pipeline-file &lt;wav&gt; [--model p] [--cancel-after-ms N] [--repeat N] [--backend cpu|auto]
///       WAV を「録音済み音声」として Coordinator → 子プロセス認識 → 投入（出力のみの fake）まで通す
/// </summary>
internal static class Diagnostics
{
    public static int NativeInfo(INativeResourceSource resources)
    {
        var native = new NativeRuntime(resources, AppPaths.RuntimeCacheRoot, AppInfo.Version, NativeRuntime.CurrentRid());
        Console.WriteLine($"resources={resources.Names.Count}");
        Console.WriteLine($"available_backends={string.Join(",", native.AvailableBackends)}");
        var sw = Stopwatch.StartNew();
        var (written, reused) = native.Extract();
        Console.WriteLine($"extract_dir={native.Directory}");
        Console.WriteLine($"extract written={written} reused={reused} ms={sw.ElapsedMilliseconds}");
        Console.WriteLine($"app_directory={AppPaths.AppDirectory}");
        Console.WriteLine($"model_path={AppPaths.DefaultModelPath}");
        return 0;
    }

    public static int PipelineFile(string[] args, INativeResourceSource resources)
    {
        string? Opt(string n) { var i = Array.IndexOf(args, n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var wav = args.Length > 1 ? args[1] : throw new ArgumentException("wav が必要です");
        var model = Opt("--model") ?? AppPaths.DefaultModelPath;
        var repeat = int.Parse(Opt("--repeat") ?? "1");
        var cancelAfter = Opt("--cancel-after-ms") is { } c ? int.Parse(c) : -1;
        var backend = Opt("--backend") ?? "auto";

        var native = new NativeRuntime(resources, AppPaths.RuntimeCacheRoot, AppInfo.Version, NativeRuntime.CurrentRid());
        var order = backend == "cpu" ? new[] { "cpu" } : new[] { "vulkan", "cpu" };
        using var recognizer = new WhisperRecognizer(new WhisperRecognizerOptions(
            model, order, native.AvailableBackends));
        var injector = new ConsoleInjector();
        using var coordinator = new SessionCoordinator(new WavFileRecorder(wav), recognizer, injector);

        var t0 = Stopwatch.StartNew();
        var done = new ManualResetEventSlim();
        var recognizing = new ManualResetEventSlim();
        coordinator.StateChanged += s =>
        {
            Console.WriteLine($"[{t0.ElapsedMilliseconds,6} ms] state={s.State}");
            if (s.State == SessionState.Recognizing) recognizing.Set();
            if (s.State is SessionState.Idle or SessionState.Failed) done.Set();
        };
        coordinator.Noticed += n => Console.WriteLine($"[notice:{n.Kind}] {n.Message}");
        coordinator.BackendReported += b => Console.WriteLine($"backend={b}");

        var exit = 0;
        for (var i = 0; i < repeat; i++)
        {
            done.Reset();
            recognizing.Reset();
            var sw = Stopwatch.StartNew();
            coordinator.Toggle();           // 録音開始
            coordinator.Toggle();           // 停止 → 認識
            if (cancelAfter >= 0)
            {
                recognizing.Wait(TimeSpan.FromSeconds(10));
                Thread.Sleep(cancelAfter);
                coordinator.Cancel();
                coordinator.FlushAsync().Wait();
                Thread.Sleep(1500);
                var workers = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Environment.ProcessPath!))
                    .Count(p => p.Id != Environment.ProcessId);
                Console.WriteLine($"cancelled_state={coordinator.Snapshot.State} injected={injector.Injected.Count} leftover_worker_processes={workers}");
                if (coordinator.Snapshot.State != SessionState.Idle || injector.Injected.Count != 0 || workers != 0) exit = 1;
                continue;
            }
            if (!done.Wait(TimeSpan.FromMinutes(5))) { Console.WriteLine("timeout"); return 1; }
            Console.WriteLine($"run{i + 1}_total_ms={sw.ElapsedMilliseconds}");
        }
        foreach (var t in injector.Injected) Console.WriteLine($"injected_text={t}");
        if (cancelAfter < 0 && injector.Injected.Count != repeat) exit = 1;
        return exit;
    }

    private sealed class ConsoleInjector : ITextInjector
    {
        public List<string> Injected { get; } = [];

        public Task<InjectResult> InjectAsync(string text, CancellationToken ct)
        {
            Injected.Add(text);
            return Task.FromResult(new InjectResult(InjectOutcome.Success));
        }
    }
}
