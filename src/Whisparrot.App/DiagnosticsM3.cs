using System.Diagnostics;
using Whisparrot.Core;
using Whisparrot.Core.Settings;
using Whisparrot.Infrastructure.Network;
using Whisparrot.Infrastructure.Polish;
using Whisparrot.Infrastructure.Settings;

namespace Whisparrot.App;

/// <summary>
/// M3（T11）の非対話診断。
///   --write-cleanup-config &lt;config.json&gt; --url U --model M [--key-env NAME] [--provider compatible|chatgpt|off]
///        整文設定を DPAPI つきで書いた config.json を作る（キーは環境変数から読み、画面にも出さない）
///   --cleanup-probe --config &lt;config.json&gt; --text &lt;認識結果&gt;
///        認識・録音を代役に置き、実際の整文クライアント・SessionCoordinator・NetworkContext で 1 セッションを通して
///        状態の遷移・通知・投入内容を出す（投入がちょうど 1 回かで合否）
/// </summary>
internal static partial class Diagnostics
{
    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    public static int WriteCleanupConfig(string[] args)
    {
        var path = args.Length > 1 ? args[1] : throw new ArgumentException("config.json のパスが必要です");
        var protector = new DpapiSecretProtector();
        var key = Arg(args, "--key-env") is { } env ? Environment.GetEnvironmentVariable(env) : null;
        var provider = (Arg(args, "--provider") ?? "compatible") switch
        {
            "off" => CleanupProvider.Off,
            "chatgpt" => CleanupProvider.ChatGpt,
            _ => CleanupProvider.Compatible,
        };
        var settings = new SettingsService(new JsonSettingsStore(path));
        settings.Update(s => s with
        {
            Cleanup = s.Cleanup with
            {
                Provider = provider,
                Compatible = new CompatibleCleanupSettings(Arg(args, "--url") ?? "", protector.Protect(key), Arg(args, "--model") ?? ""),
            },
        });
        Console.WriteLine($"written provider={provider} key={(key is null ? "none" : "dpapi")}");
        return 0;
    }

    public static int CleanupProbe(string[] args)
    {
        var configPath = Arg(args, "--config") ?? throw new ArgumentException("--config が必要です");
        var text = Arg(args, "--text") ?? throw new ArgumentException("--text が必要です");
        var protector = new DpapiSecretProtector();
        var settings = new SettingsService(new JsonSettingsStore(configPath));
        var network = new NetworkContext(() => ProxyConfig.FromSettings(settings.Current.Proxy, protector), ProxyEnvironment.Capture());
        var polisher = new SettingsTextPolisher(() => settings.Current, protector, network);
        Console.WriteLine("polish_setup=" + polisher.Prepare() switch
        {
            PolishSetup.Off => "Off",
            PolishSetup.Unconfigured u => "Unconfigured(" + u.Reason + ")",
            PolishSetup.Ready => "Ready",
            _ => "?",
        });

        var injected = new List<string>();
        using var coordinator = new SessionCoordinator(new ProbeRecorder(), new ProbeRecognizer(text), new ProbeInjector(injected), polisher);
        var t0 = Stopwatch.StartNew();
        var done = new ManualResetEventSlim();
        coordinator.StateChanged += s =>
        {
            Console.WriteLine($"[{t0.ElapsedMilliseconds,6} ms] state={s.State}");
            if (s.State is SessionState.Idle or SessionState.Failed) done.Set();
        };
        coordinator.Noticed += n => Console.WriteLine($"[notice:{n.Kind}] {n.Message}");
        coordinator.Toggle();
        coordinator.Toggle();
        if (!done.Wait(TimeSpan.FromSeconds(60))) { Console.WriteLine("timeout"); return 1; }
        Console.WriteLine($"elapsed_ms={t0.ElapsedMilliseconds}");
        Console.WriteLine($"injected_count={injected.Count}");
        foreach (var t in injected) Console.WriteLine($"injected_text={t}");
        return injected.Count == 1 ? 0 : 1;
    }

    private sealed class ProbeRecorder : IRecorder
    {
        public event Action<float>? LevelChanged { add { } remove { } }
        public event Action<RecorderError>? Failed { add { } remove { } }
        public void Start() { }
        public Task<PcmAudio> StopAsync() => Task.FromResult(new PcmAudio(new float[PcmAudio.SampleRate]));
        public void Cancel() { }
        public void Dispose() { }
    }

    private sealed class ProbeRecognizer(string text) : IRecognizer
    {
        public string? CheckReady() => null;
        public Task<RecognitionResult> RecognizeAsync(RecognitionRequest request, CancellationToken ct) => Task.FromResult(new RecognitionResult(text, "probe"));
        public void Dispose() { }
    }

    private sealed class ProbeInjector(List<string> injected) : ITextInjector
    {
        public Task<InjectResult> InjectAsync(string text, CancellationToken ct)
        {
            injected.Add(text);
            return Task.FromResult(new InjectResult(InjectOutcome.Success));
        }
    }
}
