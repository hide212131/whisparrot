using System.IO;
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
        Console.WriteLine($"route={(network.Describe(entry.Url) is { ViaProxy: true } r ? "proxy " + r.ProxyHostPort + " auth=" + r.Auth : "direct")}");
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

internal static partial class Diagnostics
{
    /// <summary>
    /// --settings-window-probe &lt;dir&gt;: 本物の SettingsWindow を（非対話セッションで）生成し、コントロールを設定して「保存」を実行する。
    /// 自動起動はテスト用のサブキーへ、ホットキー登録は記録するだけの代役へ向ける。画面の見た目・操作感は確認できない。
    /// </summary>
    public static int SettingsWindowProbe(string[] args)
    {
        var dir = args.Length > 1 ? args[1] : throw new ArgumentException("ディレクトリが必要です");
        Directory.CreateDirectory(dir);
        var ok = true;
        void Check(string name, bool pass) { Console.WriteLine($"{(pass ? "PASS" : "FAIL")} {name}"); ok &= pass; }

        var testKey = @"Software\whisparrot-test\window-probe";
        var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        app.Startup += (_, _) =>
        {
            try { ok &= RunWindowProbe(dir, testKey, Check); }
            catch (Exception ex) { Console.WriteLine($"FAIL exception {ex.GetType().Name}: {ex.Message}"); ok = false; }
            finally { app.Shutdown(); }
        };
        app.Run();
        try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\whisparrot-test", false); } catch { }
        Console.WriteLine(ok ? "settings_window_probe=PASS" : "settings_window_probe=FAIL");
        return ok ? 0 : 1;
    }

    private static bool RunWindowProbe(string dir, string testKey, Action<string, bool> check)
    {
        var protector = new DpapiSecretProtector();
        var settings = new SettingsService(new JsonSettingsStore(Path.Combine(dir, "config.json")));
        var autostart = new AutostartService(Environment.ProcessPath!, testKey);
        var network = new NetworkContext(() => ProxyConfig.FromSettings(settings.Current.Proxy, protector), ProxyEnvironment.Capture());
        var models = new Whisparrot.Infrastructure.Models.ModelManager(Path.Combine(dir, "models"), Whisparrot.Infrastructure.Models.ModelCatalog.Embedded(), network);
        var chatGpt = new Whisparrot.Infrastructure.ChatGpt.ChatGptAuthService(network, new Whisparrot.Infrastructure.ChatGpt.ChatGptTokenStore(Path.Combine(dir, "chatgpt-token.json"), protector));
        var tried = new List<Hotkey>();
        var hotkeyResult = (Ok: true, Err: 0);
        var win = new Views.SettingsWindow(settings, protector, models, network, chatGpt, autostart, hk => { tried.Add(hk); return hotkeyResult; }, () => null, () => null, null);
        win.Show();

        T F<T>(string name) => (T)typeof(Views.SettingsWindow).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(win)!;
        void Save() => typeof(Views.SettingsWindow).GetMethod("Save", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(win, null);
        string Error() => F<System.Windows.Controls.TextBlock>("_error").Text;

        F<System.Windows.Controls.CheckBox>("_ctrl").IsChecked = true;
        F<System.Windows.Controls.CheckBox>("_alt").IsChecked = true;
        F<System.Windows.Controls.CheckBox>("_win").IsChecked = false;
        F<System.Windows.Controls.ComboBox>("_key").SelectedItem = "K";
        F<System.Windows.Controls.CheckBox>("_autostartBox").IsChecked = true;
        F<System.Windows.Controls.RadioButton>("_cleanCompat").IsChecked = true;
        F<System.Windows.Controls.TextBox>("_baseUrl").Text = "https://api.example.com/v1";
        F<System.Windows.Controls.TextBox>("_cleanModel").Text = "model-x";
        F<System.Windows.Controls.PasswordBox>("_apiKey").Password = "sk-WINDOW-PLAINTEXT";
        F<System.Windows.Controls.RadioButton>("_pxManual").IsChecked = true;
        F<System.Windows.Controls.TextBox>("_pxUrl").Text = "http://proxy.example:3128";
        F<System.Windows.Controls.TextBox>("_pxUser").Text = "user1";
        F<System.Windows.Controls.PasswordBox>("_pxPass").Password = "pw-WINDOW-PLAINTEXT";
        F<Dictionary<string, System.Windows.Controls.RadioButton>>("_modelRadios")["base"].IsChecked = true;

        // 検証エラー: 不正な URL は保存されない
        F<System.Windows.Controls.TextBox>("_baseUrl").Text = "http://example.com/v1";
        Save();
        check("invalid URL is refused with a message and nothing is written", Error().Contains("base_url") && !File.Exists(settings.ConfigPath));
        F<System.Windows.Controls.TextBox>("_baseUrl").Text = "https://api.example.com/v1";

        // ホットキー登録に失敗したら保存しない
        hotkeyResult = (false, 1409);
        Save();
        check("hotkey registration failure blocks the save and shows the Win32 error", Error().Contains("1409") && !File.Exists(settings.ConfigPath) && !autostart.IsEnabled);
        hotkeyResult = (true, 0);

        win.Show();
        Save();
        var text = File.Exists(settings.ConfigPath) ? File.ReadAllText(settings.ConfigPath) : "";
        var s = new JsonSettingsStore(settings.ConfigPath).Load().Settings;
        check("saved: hotkey Ctrl+Alt+K tried and persisted", tried.Last().Display == "Ctrl+Alt+K" && s.Hotkey.Display == "Ctrl+Alt+K");
        check("saved: model/cleanup/proxy persisted", s.Recognition.Model == "base" && s.Cleanup.Provider == CleanupProvider.Compatible
            && s.Cleanup.Compatible.Model == "model-x" && s.Proxy is { Mode: ProxyMode.Manual, ManualUsername: "user1" });
        check("saved: next startup's negotiation order leads with the explicitly saved key, then the defaults",
            string.Join(",", new SettingsHotkeyStore(new SettingsService(new JsonSettingsStore(settings.ConfigPath))).NegotiationOrder().Select(c => c.Display)) == "Ctrl+Alt+K,Win+J,Ctrl+Win+J,Ctrl+Alt+J");
        check("saved: no plaintext secrets in config.json", !text.Contains("PLAINTEXT"));
        check("saved: DPAPI values decrypt to what was typed",
            protector.TryUnprotect(s.Cleanup.Compatible.ApiKeyDpapi, out var k) && k == "sk-WINDOW-PLAINTEXT"
            && protector.TryUnprotect(s.Proxy.ManualPasswordDpapi, out var pw) && pw == "pw-WINDOW-PLAINTEXT");
        check("saved: autostart written to the (test) Run key", autostart.CurrentValue == "\"" + Environment.ProcessPath + "\"");

        // 再度開いて、空のままの伏せ字欄は既存の暗号文を維持する
        var before = s.Cleanup.Compatible.ApiKeyDpapi;
        win.Close();
        var win2 = new Views.SettingsWindow(settings, protector, models, network, chatGpt, autostart, hk => (true, 0), () => null, () => null, null);
        win2.Show();
        typeof(Views.SettingsWindow).GetMethod("Save", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(win2, null);
        var s2 = new JsonSettingsStore(settings.ConfigPath).Load().Settings;
        check("re-save with empty password boxes keeps the existing ciphertext", s2.Cleanup.Compatible.ApiKeyDpapi == before && s2 == s);

        // T13: ChatGPT を選ぶと同じ領域にログイン / ログアウト・接続状態・モデル指定が出る。未ログインでも勝手にブラウザは開かない（操作を求める表示だけ）。
        win2.Close();
        var win3 = new Views.SettingsWindow(settings, protector, models, network, chatGpt, autostart, hk => (true, 0), () => null, () => null, null);
        win3.Show();
        T F3<T>(string name) => (T)typeof(Views.SettingsWindow).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(win3)!;
        check("chatgpt panel is hidden until ChatGPT is selected", F3<System.Windows.Controls.StackPanel>("_cgPanel").Visibility == System.Windows.Visibility.Collapsed);
        F3<System.Windows.Controls.RadioButton>("_cleanChatGpt").IsChecked = true;
        check("chatgpt selected while logged out: panel shows 未ログイン, login enabled, logout disabled, no login attempt started",
            F3<System.Windows.Controls.StackPanel>("_cgPanel").Visibility == System.Windows.Visibility.Visible
            && F3<System.Windows.Controls.TextBlock>("_cgStatus").Text.Contains("未ログイン")
            && F3<System.Windows.Controls.Button>("_cgLogin").IsEnabled && !F3<System.Windows.Controls.Button>("_cgLogout").IsEnabled
            && F3<System.Windows.Controls.StackPanel>("_cgPaste").Visibility == System.Windows.Visibility.Collapsed);
        F3<System.Windows.Controls.TextBox>("_cgModel").Text = " model-gpt ";
        typeof(Views.SettingsWindow).GetMethod("Save", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(win3, null);
        var s3 = new JsonSettingsStore(settings.ConfigPath).Load().Settings;
        check("chatgpt provider + trimmed model persisted; compatible settings kept", s3.Cleanup.Provider == CleanupProvider.ChatGpt && s3.Cleanup.ChatGpt.Model == "model-gpt"
            && s3.Cleanup.Compatible == s2.Cleanup.Compatible && !File.Exists(Path.Combine(dir, "chatgpt-token.json")));
        return true;
    }
}
