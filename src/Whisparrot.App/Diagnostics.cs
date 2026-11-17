using System.IO;
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
internal static partial class Diagnostics
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

    /// <summary>--record-probe [ms] [--cancel]: 実マイクを短時間録音し、サンプル数・レベル通知回数を表示する。</summary>
    public static int RecordProbe(string[] args)
    {
        var ms = args.Length > 1 && int.TryParse(args[1], out var v) ? v : 1500;
        var cancel = args.Contains("--cancel");
        using var rec = new WasapiRecorder();
        var levels = 0;
        float peak = 0;
        rec.LevelChanged += l => { levels++; peak = Math.Max(peak, l); };
        rec.Failed += e => Console.WriteLine($"recorder_failed={e.Kind} {e.Message}");
        try
        {
            rec.Start();
        }
        catch (RecorderException ex)
        {
            Console.WriteLine($"start_failed kind={ex.Kind} message={ex.Message} inner={ex.InnerException?.GetType().Name}: {ex.InnerException?.Message}");
            return 2;
        }
        Console.WriteLine("recording");
        Thread.Sleep(ms);
        if (cancel)
        {
            rec.Cancel();
            Console.WriteLine("cancelled (device released)");
            return 0;
        }
        var sw = Stopwatch.StartNew();
        var pcm = rec.StopAsync().GetAwaiter().GetResult();
        Console.WriteLine($"samples={pcm.Samples.Length} seconds={pcm.Duration.TotalSeconds:F2} stop_ms={sw.ElapsedMilliseconds} level_events={levels} peak_level={peak:F3}");
        return 0;
    }

    /// <summary>
    /// --clipboard-probe: クリップボードの退避/復元の往復を、複数形式で確認する（貼り付けは送らない）。
    /// --clipboard-probe --current: 今のクリップボードで退避できる形式数と、飛ばされる形式 ID を出す（変更しない）。
    /// </summary>
    public static int ClipboardProbe(string[] args)
    {
        if (args.Contains("--current"))
        {
            var c = Whisparrot.App.Windows.TextInjector.ClipboardInspectProbe();
            Console.WriteLine(c);
            return c.StartsWith("FAIL") ? 1 : 0;
        }
        var r = Whisparrot.App.Windows.TextInjector.ClipboardRoundTripProbe();
        Console.WriteLine(r);
        return r.Contains("all_original_formats_restored=True") && r.Contains("other_app_copy_preserved=True") ? 0 : 1;
    }

    /// <summary>--pill-probe: 非対話でもできる範囲でピルの生成・表示・スタイルを確認する（描画の目視はできない）。</summary>
    public static int PillProbe()
    {
        var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        var exit = 1;
        app.Startup += (_, _) =>
        {
            var pill = new Whisparrot.App.Views.PillWindow();
            var fg = Whisparrot.App.Windows.NativeMethods.GetForegroundWindow();
            foreach (var st in new[] { SessionState.Recording, SessionState.Recognizing, SessionState.Inserting, SessionState.Failed })
            {
                pill.ShowState(st);
                var hwnd = new System.Windows.Interop.WindowInteropHelper(pill).Handle;
                var ex = Whisparrot.App.Windows.NativeMethods.GetWindowLongPtr(hwnd, Whisparrot.App.Windows.NativeMethods.GWL_EXSTYLE).ToInt64();
                var ma = Whisparrot.App.Windows.NativeMethods.SendMessage(hwnd, Whisparrot.App.Windows.NativeMethods.WM_MOUSEACTIVATE, IntPtr.Zero, IntPtr.Zero).ToInt64();
                Whisparrot.App.Windows.NativeMethods.GetWindowRect(hwnd, out var rc);
                Console.WriteLine($"{st}: visible={pill.IsVisible} noactivate={(ex & Whisparrot.App.Windows.NativeMethods.WS_EX_NOACTIVATE) != 0} toolwindow={(ex & Whisparrot.App.Windows.NativeMethods.WS_EX_TOOLWINDOW) != 0} wm_mouseactivate={ma} (3=MA_NOACTIVATE) rect=({rc.Left},{rc.Top})-({rc.Right},{rc.Bottom}) active={pill.IsActive}");
            }
            var fgAfter = Whisparrot.App.Windows.NativeMethods.GetForegroundWindow();
            Console.WriteLine($"foreground_unchanged={fg == fgAfter}");
            pill.ShowState(SessionState.Idle);
            Console.WriteLine($"hidden_when_idle={!pill.IsVisible}");
            exit = 0;
            app.Shutdown();
        };
        app.Run();
        return exit;
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
        // --use-settings: config.json の recognition.model と固定 catalog（SHA-256 照合つき）でモデルを解決する（T07/T09 の結線の確認）。
        Func<ModelTarget>? resolver = null;
        Whisparrot.Infrastructure.Settings.SettingsService? usedSettings = null;
        if (args.Contains("--use-settings"))
        {
            var settings = new Whisparrot.Infrastructure.Settings.SettingsService(new Whisparrot.Infrastructure.Settings.JsonSettingsStore(Opt("--config") ?? AppPaths.ConfigPath));
            usedSettings = settings;
            var net = new Whisparrot.Infrastructure.Network.NetworkContext(() => Whisparrot.Infrastructure.Network.ProxyConfig.Environment, Whisparrot.Infrastructure.Network.ProxyEnvironment.Capture());
            var models = new Whisparrot.Infrastructure.Models.ModelManager(AppPaths.ModelsDirectory, Whisparrot.Infrastructure.Models.ModelCatalog.Embedded(), net);
            resolver = () =>
            {
                var id = settings.Current.Recognition.Model;
                var reason = models.CheckReady(id);
                Console.WriteLine($"model_id={id} ready={(reason is null ? "yes" : reason)}");
                return new ModelTarget(models.PathOf(id), reason);
            };
        }
        using var recognizer = new WhisperRecognizer(new WhisperRecognizerOptions(
            model, order, native.AvailableBackends, ModelResolver: resolver));
        var injector = new ConsoleInjector();
        // --use-settings のときは config.json の整文設定（cleanup.provider=compatible など）も実際に使う（T11 の結線の確認）。
        ITextPolisher? polisher = null;
        if (usedSettings is not null)
        {
            var protector = new Whisparrot.Infrastructure.Settings.DpapiSecretProtector();
            var cleanupNet = new Whisparrot.Infrastructure.Network.NetworkContext(() => Whisparrot.Infrastructure.Network.ProxyConfig.FromSettings(usedSettings.Current.Proxy, protector), Whisparrot.Infrastructure.Network.ProxyEnvironment.Capture());
            polisher = new Whisparrot.Infrastructure.Polish.SettingsTextPolisher(() => usedSettings.Current, protector, cleanupNet);
        }
        using var coordinator = new SessionCoordinator(new WavFileRecorder(wav), recognizer, injector, polisher);

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
        Console.WriteLine($"worker_detail={recognizer.LastWorkerDetail}");
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
