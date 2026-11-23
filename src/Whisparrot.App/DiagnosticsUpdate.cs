using System.IO;
using System.Diagnostics;
using Whisparrot.Core.Settings;
using Whisparrot.Core.Update;
using Whisparrot.Infrastructure;
using Whisparrot.Infrastructure.Network;
using Whisparrot.Infrastructure.Settings;
using Whisparrot.Infrastructure.Update;

namespace Whisparrot.App;

/// <summary>
/// 自動更新の非対話診断（トレイ・設定画面と同じ UpdateService / ExeSwapper を使う）。
///   --update-probe check [--stable]           GitHub Releases を確認し、候補（版・資産・digest）を表示する。
///   --update-probe apply-real [--stable]      確認→ダウンロード→SHA-256 照合→入れ替え→新版を起動、までを通す。
///   --update-probe apply-local &lt;新 exe&gt;      指定ファイルを &lt;exe&gt;.new として置き、入れ替え→新版を起動する（ネットワークなし）。
/// いずれも現在の exe を対象にする。出力は版・可否・パスだけ。
/// </summary>
internal static partial class Diagnostics
{
    public static int UpdateProbe(string[] args)
    {
        var mode = args.Length > 1 ? args[1] : "check";
        var stable = args.Contains("--stable");
        var exe = Environment.ProcessPath!;
        var plan = SwapPlan.For(exe);
        Console.WriteLine($"version={AppInfo.DisplayVersion} arch={UpdateService.CurrentArch()} exe={exe}");

        if (mode == "apply-local")
        {
            if (args.Length < 3 || !File.Exists(args[2])) { Console.WriteLine("usage: --update-probe apply-local <new exe>"); return 2; }
            File.Copy(args[2], plan.NewPath, overwrite: true);
            return Swap(plan);
        }

        var settings = new SettingsService(new JsonSettingsStore(AppPaths.ConfigPath));
        if (stable) settings.Update(s => s with { Updates = s.Updates with { IncludePrerelease = false } });
        var network = new NetworkContext(() => ProxyConfig.FromSettings(settings.Current.Proxy, new DpapiSecretProtector()), ProxyEnvironment.Capture());
        SemVer.TryParse(AppInfo.DisplayVersion, out var current);
        var updater = new UpdateService(settings, new ReleaseClient(network), current, plan);

        updater.CheckAsync().GetAwaiter().GetResult();
        Console.WriteLine($"state={updater.State} message={updater.Message}");
        if (updater.Candidate is not { } c) return mode == "check" ? 0 : 1;
        Console.WriteLine($"candidate={c.Tag} prerelease={c.Prerelease} asset={c.AssetName} size={c.Size} sha256={c.Sha256}");
        if (mode == "check") return 0;

        var sw = Stopwatch.StartNew();
        updater.DownloadAsync().GetAwaiter().GetResult();
        Console.WriteLine($"download_state={updater.State} elapsed_ms={sw.ElapsedMilliseconds} message={updater.Message}");
        if (updater.State != UpdateState.Ready) return 1;
        Console.WriteLine($"new_file_sha256={Sha256Digest.ComputeAsync(plan.NewPath).GetAwaiter().GetResult()} size={new FileInfo(plan.NewPath).Length}");
        return Swap(plan);
    }

    private static int Swap(SwapPlan plan)
    {
        UpdateLauncher.Preload();
        var result = ExeSwapper.Apply(plan, () => UpdateLauncher.Start(plan));
        Console.WriteLine($"update_swap={result.Outcome} detail={result.Detail}");
        return result.Outcome == SwapOutcome.Swapped ? 0 : 1;
    }
}
