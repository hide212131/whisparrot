using System.Security.Cryptography;
using Whisparrot.Core;
using Whisparrot.Core.Settings;
using Whisparrot.Infrastructure.Settings;

namespace Whisparrot.Tests;

/// <summary>
/// 司令塔決定: 交渉で採用されたキーは config.json に保存しない。永続化されるのは、設定ウィンドウで利用者が
/// 明示的に選んで保存したキー（= 設定の保存）だけ。起動時の交渉順は「設定のキー → 既定候補順（重複除く）」。
/// </summary>
public sealed class HotkeyPersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "whisparrot-hotkey-" + Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_dir, "config.json");

    public HotkeyPersistenceTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private SettingsService NewService() => new(new JsonSettingsStore(ConfigPath));

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>TrayApp の起動処理と同じ呼び方（交渉順を作って登録を試すだけ。設定には触れない）。</summary>
    private static (Hotkey? Adopted, List<string> Tried) Startup(SettingsService svc, Func<Hotkey, bool> tryRegister)
    {
        var tried = new List<string>();
        var adopted = HotkeyNegotiator.Negotiate(new SettingsHotkeyStore(svc).NegotiationOrder(), c => { tried.Add(c.Display); return tryRegister(c); });
        return (adopted, tried);
    }

    // (a) 初回起動で Win+J が取れず Ctrl+Win+J が採用されても config.json は作られない／変わらない
    [Fact]
    public void First_run_where_Win_J_is_taken_adopts_Ctrl_Win_J_without_creating_config_json()
    {
        var svc = NewService();
        var (adopted, tried) = Startup(svc, c => c.Display != "Win+J");

        Assert.Equal("Ctrl+Win+J", adopted?.Display);
        Assert.Equal(["Win+J", "Ctrl+Win+J"], tried);
        Assert.False(File.Exists(ConfigPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_dir));
        Assert.Equal("Win+J", svc.Current.Hotkey.Display); // 設定上のキーは変わらない
    }

    [Fact]
    public void An_existing_config_json_is_byte_identical_after_a_fallback_adoption()
    {
        File.WriteAllText(ConfigPath, "{\n  \"schema_version\": 1,\n  \"recognition\": { \"model\": \"base\" }\n}\n");
        var before = Hash(ConfigPath);
        var svc = NewService();

        var (adopted, _) = Startup(svc, c => c.Display == "Ctrl+Alt+J");

        Assert.Equal("Ctrl+Alt+J", adopted?.Display);
        Assert.Equal(before, Hash(ConfigPath));
        Assert.Equal("Win+J", svc.Current.Hotkey.Display);
    }

    [Fact]
    public void Total_failure_adopts_nothing_and_writes_nothing()
    {
        var svc = NewService();
        var (adopted, tried) = Startup(svc, _ => false);
        Assert.Null(adopted);
        Assert.Equal(["Win+J", "Ctrl+Win+J", "Ctrl+Alt+J"], tried);
        Assert.False(File.Exists(ConfigPath));
    }

    // (b) 設定ウィンドウで明示的に選んだキーは保存され、次回そのキーが先頭候補になる
    [Fact]
    public void A_key_explicitly_saved_from_settings_persists_and_leads_the_next_negotiation()
    {
        var svc = NewService();
        var chosen = HotkeyFormat.Create(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x4B); // Ctrl+Alt+K（既定候補にない）
        svc.Update(s => s with { Hotkey = chosen }); // SettingsWindow.Save が行う保存と同じ

        Assert.True(File.Exists(ConfigPath));
        var next = NewService(); // 次回起動
        Assert.Equal("Ctrl+Alt+K", next.Current.Hotkey.Display);
        Assert.Equal(["Ctrl+Alt+K", "Win+J", "Ctrl+Win+J", "Ctrl+Alt+J"], new SettingsHotkeyStore(next).NegotiationOrder().Select(c => c.Display));

        var (adopted, tried) = Startup(next, _ => true);
        Assert.Equal("Ctrl+Alt+K", adopted?.Display);
        Assert.Equal(["Ctrl+Alt+K"], tried);
    }

    [Fact]
    public void An_explicitly_chosen_default_candidate_is_not_duplicated_in_the_order()
    {
        var svc = NewService();
        svc.Update(s => s with { Hotkey = HotkeyFormat.Create(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x4A) });
        Assert.Equal(["Ctrl+Alt+J", "Win+J", "Ctrl+Win+J"], new SettingsHotkeyStore(NewService()).NegotiationOrder().Select(c => c.Display));
    }

    // (c) 一時失敗の次回起動では再び Win+J が先頭候補として試される
    [Fact]
    public void After_a_transient_failure_the_next_startup_tries_Win_J_first_again()
    {
        var svc = NewService();
        var (first, _) = Startup(svc, c => c.Display != "Win+J"); // 一時的に他アプリが占有
        Assert.Equal("Ctrl+Win+J", first?.Display);

        var restarted = NewService(); // 再起動（占有は解消）
        var (second, tried) = Startup(restarted, _ => true);
        Assert.Equal("Win+J", second?.Display);
        Assert.Equal(["Win+J"], tried);
    }

    [Fact]
    public void Saving_unrelated_settings_after_a_fallback_keeps_the_configured_key_not_the_adopted_one()
    {
        // 設定ウィンドウはコントロール（= 設定のキー）から保存する。採用キーが保存へ混入しないこと。
        var svc = NewService();
        Startup(svc, c => c.Display != "Win+J"); // Ctrl+Win+J が採用された状態
        svc.Update(s => s with { Recognition = s.Recognition with { Model = ModelIds.Base } });
        Assert.Equal("Win+J", NewService().Current.Hotkey.Display);
        Assert.Equal("base", NewService().Current.Recognition.Model);
    }

    [Fact]
    public void A_config_that_failed_to_load_is_still_never_written_by_negotiation()
    {
        File.WriteAllText(ConfigPath, "{ broken");
        var before = Hash(ConfigPath);
        var svc = NewService();
        Startup(svc, c => c.Display != "Win+J");
        Assert.Equal(before, Hash(ConfigPath));
        Assert.Single(Directory.EnumerateFileSystemEntries(_dir));
    }
}
