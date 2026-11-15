using System.Diagnostics;
using System.Security.Cryptography;
using Whisparrot.Core;
using Whisparrot.Core.Settings;
using Whisparrot.Infrastructure;
using Whisparrot.Infrastructure.Models;
using Whisparrot.Infrastructure.Network;
using Whisparrot.Infrastructure.Settings;

namespace Whisparrot.App;

/// <summary>
/// M2（T07〜T09）の非対話診断。
///   --settings-probe &lt;dir&gt;                 設定の往復・DPAPI・未来 schema の非破壊拒否・壊れた JSON の復旧を実ファイルで確認
///   --autostart-probe                      実際の HKCU Run キーの ON/OFF（確認は reg.exe で別経路から読む）
///   --model-download &lt;id&gt; [--dir d]        T08 の NetworkContext 経由で取得し、SHA-256 照合して配置する
/// </summary>
internal static partial class Diagnostics
{
    public static int SettingsProbe(string[] args)
    {
        var dir = args.Length > 1 ? args[1] : throw new ArgumentException("ディレクトリが必要です");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        var ok = true;
        void Check(string name, bool pass) { Console.WriteLine($"{(pass ? "PASS" : "FAIL")} {name}"); ok &= pass; }
        var protector = new DpapiSecretProtector();
        const string secret = "sk-probe-PLAINTEXT-0123456789";
        const string proxyPw = "proxy-PLAINTEXT-pw";

        var store = new JsonSettingsStore(path);
        var r0 = store.Load();
        Check("missing file -> defaults, not created", r0.Status == SettingsLoadStatus.Missing && !File.Exists(path));

        var s = AppSettings.Default with
        {
            Hotkey = HotkeyFormat.Create(HotkeyModifiers.Control | HotkeyModifiers.Win, 0x4A),
            Recognition = new RecognitionSettings(ModelIds.Base, "ja"),
            Autostart = true,
            Cleanup = AppSettings.Default.Cleanup with
            {
                Provider = CleanupProvider.Compatible,
                Compatible = new CompatibleCleanupSettings("https://api.example.com/v1", protector.Protect(secret), "m-1"),
            },
            Proxy = new ProxySettings(ProxyMode.Manual, "http://proxy.example:3128", "user", protector.Protect(proxyPw)),
        };
        store.Save(s);
        var text = File.ReadAllText(path);
        Check("saved file exists, no leftover tmp", File.Exists(path) && !Directory.EnumerateFiles(dir, "*.tmp").Any());
        Check("plaintext secrets absent from config.json", !text.Contains(secret) && !text.Contains(proxyPw));
        Check("api_key_dpapi is Base64 ciphertext", s.Cleanup.Compatible.ApiKeyDpapi is { Length: > 40 } && text.Contains(s.Cleanup.Compatible.ApiKeyDpapi!));

        var reloaded = new JsonSettingsStore(path).Load();
        Check("round trip equals", reloaded.Status == SettingsLoadStatus.Loaded && reloaded.Settings == s);
        Check("DPAPI decrypts same user", protector.TryUnprotect(reloaded.Settings.Cleanup.Compatible.ApiKeyDpapi, out var back) && back == secret);

        // 未来 schema: 非破壊で拒否
        var future = "{\n  \"schema_version\": 2,\n  \"something_new\": true\n}\n";
        File.WriteAllText(path, future);
        var before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        var fs = new JsonSettingsStore(path);
        var fr = fs.Load();
        var blocked = false;
        try { fs.Save(AppSettings.Default); } catch (SettingsBlockedException) { blocked = true; }
        var after = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        Check("future schema -> UnsupportedSchema", fr.Status == SettingsLoadStatus.UnsupportedSchema);
        Check("future schema: save refused, file byte-identical", blocked && before == after);

        // 壊れた JSON: 無断で初期化しない → 明示操作で退避
        File.WriteAllText(path, "{ not json");
        var cs = new JsonSettingsStore(path);
        var cr = cs.Load();
        Check("corrupt JSON -> Corrupt, file untouched", cr.Status == SettingsLoadStatus.Corrupt && File.ReadAllText(path) == "{ not json");
        var backup = cs.BackupAndReset();
        Check("explicit reset moves the broken file aside", File.Exists(backup) && File.ReadAllText(backup) == "{ not json" && !File.Exists(path));
        cs.Save(AppSettings.Default);
        Check("save works again after reset", new JsonSettingsStore(path).Load().Status == SettingsLoadStatus.Loaded);

        Console.WriteLine(ok ? "settings_probe=PASS" : "settings_probe=FAIL");
        return ok ? 0 : 1;
    }

    public static int AutostartProbe()
    {
        var svc = new AutostartService(Environment.ProcessPath!);
        var ok = true;
        void Check(string name, bool pass) { Console.WriteLine($"{(pass ? "PASS" : "FAIL")} {name}"); ok &= pass; }

        string RegQuery()
        {
            var psi = new ProcessStartInfo("reg.exe", ["query", @"HKCU\" + AutostartService.DefaultRunKey, "/v", "whisparrot"])
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0 ? o : "";
        }

        var wasEnabled = svc.IsEnabled;
        var previous = svc.CurrentValue;
        svc.Apply(true);
        var on = RegQuery();
        Console.WriteLine("reg query (ON): " + on.Replace("\r", "").Replace("\n", " | ").Trim());
        Check("ON: value is the quoted absolute exe path (read via reg.exe)", on.Contains("\"" + Environment.ProcessPath + "\""));
        svc.Apply(false);
        Check("OFF: value removed (read via reg.exe)", RegQuery().Length == 0);
        svc.Apply(false);
        Check("OFF is idempotent", !svc.IsEnabled);
        if (wasEnabled && previous is not null) // 元の状態へ戻す
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(AutostartService.DefaultRunKey);
            key.SetValue("whisparrot", previous);
        }
        Console.WriteLine(ok ? "autostart_probe=PASS" : "autostart_probe=FAIL");
        return ok ? 0 : 1;
    }

    public static int ModelDownload(string[] args)
    {
        string? Opt(string n) { var i = Array.IndexOf(args, n); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var id = args.Length > 1 ? args[1] : throw new ArgumentException("モデル ID が必要です");
        var dir = Opt("--dir") ?? AppPaths.ModelsDirectory;
        var network = new NetworkContext(() => ProxyConfig.Environment, ProxyEnvironment.Capture());
        var manager = new ModelManager(dir, ModelCatalog.Embedded(), network);
        var entry = manager.Catalog.Get(id);
        Console.WriteLine($"model={id} url={entry.Url} size={entry.SizeBytes} sha256={entry.Sha256}");
        Console.WriteLine($"route={(network.Describe(entry.Url) is { ViaProxy: true } r ? "proxy " + r.ProxyHostPort : "direct")}");
        var sw = Stopwatch.StartNew();
        var last = -1;
        var progress = new Progress<DownloadProgress>(p =>
        {
            var pct = (int)(p.BytesDone * 100 / Math.Max(1, p.BytesTotal));
            if (pct / 10 != last / 10) { last = pct; Console.WriteLine($"  {pct}% ({p.BytesDone / (1024 * 1024)} MiB)"); }
        });
        try
        {
            manager.DownloadAsync(id, progress, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"download_failed={ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        Console.WriteLine($"download_ms={sw.ElapsedMilliseconds}");
        var st = manager.GetStatusAsync(id).GetAwaiter().GetResult();
        Console.WriteLine($"status={st.State} part_exists={File.Exists(manager.PathOf(id) + ".part")}");
        return st.State == ModelState.Ready ? 0 : 1;
    }
}
