using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Whisparrot.Core;
using Whisparrot.Core.Settings;
using Whisparrot.Infrastructure.Settings;

namespace Whisparrot.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "whisparrot-settings-" + Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_dir, "config.json");
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    public SettingsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private JsonSettingsStore NewStore() => new(ConfigPath, () => Now);

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    // ---- 既定値と §8.1 のスキーマ ----

    [Fact]
    public void Defaults_match_design_8_1()
    {
        var d = AppSettings.Default;
        Assert.Equal(1, d.SchemaVersion);
        Assert.Equal("Win+J", d.Hotkey.Display);
        Assert.Null(d.MicrophoneId);
        Assert.Equal(("small", "ja"), (d.Recognition.Model, d.Recognition.Language));
        Assert.Equal(CleanupProvider.Off, d.Cleanup.Provider);
        Assert.Equal(15, d.Cleanup.TimeoutSeconds);
        Assert.Equal(("", null, ""), (d.Cleanup.Compatible.BaseUrl, d.Cleanup.Compatible.ApiKeyDpapi, d.Cleanup.Compatible.Model));
        Assert.Equal("", d.Cleanup.ChatGpt.Model);
        Assert.Equal((ProxyMode.Environment, "", "", null), (d.Proxy.Mode, d.Proxy.ManualUrl, d.Proxy.ManualUsername, d.Proxy.ManualPasswordDpapi));
        Assert.False(d.Autostart);
        Assert.Equal(InsertionMethod.Clipboard, d.Insertion.Method);
        Assert.Null(d.Updates.LastCheckAttemptUtc);
        Assert.Null(d.Updates.LastCheckSuccessUtc);
    }

    [Fact]
    public void Serialized_defaults_contain_every_key_of_the_schema()
    {
        var json = Encoding.UTF8.GetString(SettingsJson.Serialize(AppSettings.Default));
        using var doc = JsonDocument.Parse(json);
        string[] expected =
        [
            "schema_version", "hotkey.modifiers", "hotkey.key", "microphone_id", "recognition.model", "recognition.language",
            "cleanup.provider", "cleanup.timeout_seconds", "cleanup.compatible.base_url", "cleanup.compatible.api_key_dpapi",
            "cleanup.compatible.model", "cleanup.chatgpt.model", "proxy.mode", "proxy.manual_url", "proxy.manual_username",
            "proxy.manual_password_dpapi", "autostart", "insertion.method", "updates.last_check_attempt_utc", "updates.last_check_success_utc",
        ];
        foreach (var path in expected)
        {
            var el = doc.RootElement;
            foreach (var part in path.Split('.')) Assert.True(el.TryGetProperty(part, out el), $"missing {path}");
        }
        Assert.Equal("Win", doc.RootElement.GetProperty("hotkey").GetProperty("modifiers")[0].GetString());
        Assert.False(json.StartsWith('\uFEFF'));
    }

    [Fact]
    public void Empty_object_with_only_schema_version_fills_defaults()
    {
        var r = SettingsJson.Parse("{\"schema_version\":1}", Now);
        Assert.Equal(SettingsLoadStatus.Loaded, r.Status);
        Assert.Equal(AppSettings.Default, r.Settings);
    }

    [Fact]
    public void Round_trip_preserves_every_field()
    {
        var s = AppSettings.Default with
        {
            Hotkey = HotkeyFormat.Create(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x71),
            MicrophoneId = "{0.0.1.00000000}.{abc}",
            Recognition = new RecognitionSettings("large-v3-turbo", "ja"),
            Cleanup = new CleanupSettings(CleanupProvider.Compatible, 15,
                new CompatibleCleanupSettings("https://api.example.com/v1", "AAAA", "gpt-x"), new ChatGptCleanupSettings("m")),
            Proxy = new ProxySettings(ProxyMode.Manual, "http://proxy.local:3128", "u", "BBBB"),
            Autostart = true,
            Insertion = new InsertionSettings(InsertionMethod.SendInput),
            Updates = new UpdateSettings(Now.AddHours(-1), Now.AddDays(-1)),
        };
        var parsed = SettingsJson.Parse(Encoding.UTF8.GetString(SettingsJson.Serialize(s)), Now);
        Assert.Equal(SettingsLoadStatus.Loaded, parsed.Status);
        Assert.Equal(s, parsed.Settings);
    }

    // ---- 型・値の検証 ----

    [Theory]
    [InlineData("{\"schema_version\":1,\"hotkey\":null}", "hotkey")]
    [InlineData("{\"schema_version\":1,\"recognition\":null}", "recognition")]
    [InlineData("{\"schema_version\":1,\"recognition\":{\"model\":\"tiny\"}}", "recognition.model")]
    [InlineData("{\"schema_version\":1,\"recognition\":{\"language\":\"en\"}}", "recognition.language")]
    [InlineData("{\"schema_version\":1,\"recognition\":{\"model\":5}}", "recognition.model")]
    [InlineData("{\"schema_version\":1,\"cleanup\":{\"provider\":\"gemini\"}}", "cleanup.provider")]
    [InlineData("{\"schema_version\":1,\"cleanup\":{\"timeout_seconds\":30}}", "cleanup.timeout_seconds")]
    [InlineData("{\"schema_version\":1,\"cleanup\":{\"compatible\":{\"base_url\":\"http://example.com/v1\"}}}", "cleanup.compatible.base_url")]
    [InlineData("{\"schema_version\":1,\"cleanup\":{\"provider\":\"compatible\"}}", "cleanup.compatible.base_url")]
    [InlineData("{\"schema_version\":1,\"proxy\":{\"mode\":\"manual\"}}", "proxy.manual_url")]
    [InlineData("{\"schema_version\":1,\"proxy\":{\"manual_url\":\"http://u:p@h:1\"}}", "proxy.manual_url")]
    [InlineData("{\"schema_version\":1,\"autostart\":\"yes\"}", "autostart")]
    [InlineData("{\"schema_version\":1,\"insertion\":{\"method\":\"paste\"}}", "insertion.method")]
    [InlineData("{\"schema_version\":1,\"unknown_key\":1}", "unknown_key")]
    [InlineData("{\"schema_version\":1,\"cleanup\":{\"compatible\":{\"extra\":1}}}", "cleanup.compatible.extra")]
    [InlineData("{\"schema_version\":1,\"hotkey\":{\"modifiers\":[],\"key\":\"J\"}}", "hotkey")]
    [InlineData("{\"schema_version\":1,\"hotkey\":{\"modifiers\":[\"Win\",\"Win\"],\"key\":\"J\"}}", "hotkey")]
    [InlineData("{\"schema_version\":1,\"hotkey\":{\"modifiers\":[\"Win\"],\"key\":\"Shift\"}}", "hotkey")]
    [InlineData("{\"schema_version\":1,\"hotkey\":{\"modifiers\":[\"Win\"]}}", "hotkey")]
    [InlineData("{\"schema_version\":1,\"updates\":{\"last_check_attempt_utc\":\"yesterday\"}}", "updates.last_check_attempt_utc")]
    [InlineData("{\"schema_version\":1,\"updates\":{\"last_check_success_utc\":\"2027-01-01T00:00:00Z\"}}", "updates.last_check_success_utc")]
    [InlineData("{\"schema_version\":1,\"microphone_id\":\"\"}", "microphone_id")]
    [InlineData("{\"schema_version\":1,\"autostart\":true,\"autostart\":false}", "autostart")]
    public void Invalid_values_are_rejected_with_the_json_path(string json, string path)
    {
        var r = SettingsJson.Parse(json, Now);
        Assert.Equal(SettingsLoadStatus.Invalid, r.Status);
        Assert.Contains(r.Issues, i => i.Path == path);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"hotkey\":{}}")]
    [InlineData("{\"schema_version\":null}")]
    [InlineData("{\"schema_version\":\"1\"}")]
    [InlineData("{\"schema_version\":0}")]
    [InlineData("{\"schema_version\":-3}")]
    [InlineData("{\"schema_version\":1.5}")]
    [InlineData("[]")]
    public void Missing_or_malformed_schema_version_is_an_error_not_a_guess(string json)
    {
        var r = SettingsJson.Parse(json, Now);
        Assert.Equal(SettingsLoadStatus.Invalid, r.Status);
        Assert.False(r.Usable);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("{\"schema_version\":1,}")]
    [InlineData("{\"schema_version\":1 /* c */}")]
    public void Broken_json_is_corrupt(string json)
    {
        Assert.Equal(SettingsLoadStatus.Corrupt, SettingsJson.Parse(json, Now).Status);
    }

    [Fact]
    public void Valid_manual_proxy_and_loopback_http_endpoint_are_accepted()
    {
        var r = SettingsJson.Parse("""
            {"schema_version":1,"cleanup":{"provider":"compatible","compatible":{"base_url":"http://localhost:8000/v1","model":"m"}},
             "proxy":{"mode":"manual","manual_url":"http://proxy.local:3128"}}
            """, Now);
        Assert.Equal(SettingsLoadStatus.Loaded, r.Status);
    }

    [Fact]
    public void Unimplemented_chatgpt_provider_is_kept_as_is()
    {
        var r = SettingsJson.Parse("{\"schema_version\":1,\"cleanup\":{\"provider\":\"chatgpt\"}}", Now);
        Assert.Equal(CleanupProvider.ChatGpt, r.Settings.Cleanup.Provider);
    }

    // ---- 未来 schema の非破壊拒否 ----

    [Fact]
    public void Future_schema_is_rejected_even_with_unknown_keys_and_the_file_is_never_touched()
    {
        var future = "{\"schema_version\":2,\"brand_new\":{\"x\":1},\"hotkey\":\"changed shape\"}\n";
        File.WriteAllText(ConfigPath, future);
        var before = Hash(ConfigPath);

        var store = NewStore();
        var r = store.Load();
        Assert.Equal(SettingsLoadStatus.UnsupportedSchema, r.Status);
        Assert.Equal(2, r.FoundSchemaVersion);
        Assert.False(r.Usable);
        Assert.Contains("新しい版", r.Describe());

        Assert.Throws<SettingsBlockedException>(() => store.Save(AppSettings.Default));
        Assert.Throws<SettingsBlockedException>(() => store.BackupAndReset());
        Assert.Equal(before, Hash(ConfigPath));
        Assert.DoesNotContain(Directory.EnumerateFiles(_dir), f => f != ConfigPath);
    }

    [Fact]
    public void Service_does_not_overwrite_when_load_failed_and_no_adopted_hotkey_is_written()
    {
        File.WriteAllText(ConfigPath, "{ broken");
        var before = Hash(ConfigPath);
        var svc = new SettingsService(NewStore());
        Assert.False(svc.IsWritable);
        Assert.Equal(AppSettings.Default, svc.Current);
        new SettingsHotkeyStore(svc).Record(HotkeyFormat.Create(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x4A), []);
        Assert.Throws<SettingsBlockedException>(() => svc.Update(s => s with { Autostart = true }));
        Assert.Equal(before, Hash(ConfigPath));
    }

    [Fact]
    public void Save_re_checks_the_disk_schema_just_before_writing()
    {
        var store = NewStore();
        store.Load();
        store.Save(AppSettings.Default with { Autostart = true });
        var newer = "{\"schema_version\":3}\n";
        File.WriteAllText(ConfigPath, newer); // 別の（新しい）版が書いた
        Assert.Throws<SettingsBlockedException>(() => store.Save(AppSettings.Default));
        Assert.Equal(newer, File.ReadAllText(ConfigPath));
        Assert.False(store.CanSave);
    }

    // ---- 壊れたファイルからの復旧 ----

    [Fact]
    public void Corrupt_file_is_left_alone_until_the_user_explicitly_resets()
    {
        File.WriteAllText(ConfigPath, "{ not json");
        var store = NewStore();
        Assert.Equal(SettingsLoadStatus.Corrupt, store.Load().Status);
        Assert.Equal("{ not json", File.ReadAllText(ConfigPath));

        var backup = store.BackupAndReset();
        Assert.Equal("{ not json", File.ReadAllText(backup));
        Assert.Contains("corrupt-20261115T120000Z", backup);
        Assert.False(File.Exists(ConfigPath));

        store.Save(AppSettings.Default);
        Assert.Equal(SettingsLoadStatus.Loaded, NewStore().Load().Status);
        Assert.Equal("{ not json", File.ReadAllText(backup)); // 退避は上書きされない
    }

    [Fact]
    public void Reset_is_refused_when_the_file_is_actually_fine()
    {
        var store = NewStore();
        store.Load();
        store.Save(AppSettings.Default);
        Assert.Throws<SettingsBlockedException>(() => store.BackupAndReset());
    }

    [Fact]
    public void Leftover_temp_files_from_a_crashed_save_are_cleaned_and_the_original_survives()
    {
        var store = NewStore();
        store.Load();
        store.Save(AppSettings.Default with { Autostart = true });
        File.WriteAllText(ConfigPath + ".deadbeef.tmp", "half written");
        var reloaded = NewStore().Load();
        Assert.True(reloaded.Settings.Autostart);
        Assert.False(File.Exists(ConfigPath + ".deadbeef.tmp"));
    }

    [Fact]
    public void Missing_file_is_not_created_by_load()
    {
        var r = NewStore().Load();
        Assert.Equal(SettingsLoadStatus.Missing, r.Status);
        Assert.True(r.Usable);
        Assert.False(File.Exists(ConfigPath));
    }

    // ---- atomic save ----

    [Fact]
    public void Failed_replace_keeps_the_original_and_leaves_no_temp_file()
    {
        var store = NewStore();
        store.Load();
        var original = AppSettings.Default with { Autostart = true };
        store.Save(original);
        var before = File.ReadAllText(ConfigPath);

        // 別プロセスが読み取り中で置換できない状況を再現する（削除・書込みを許さない共有）。
        using (new FileStream(ConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<IOException>(() => store.Save(original with { Autostart = false }));
        }
        Assert.Equal(before, File.ReadAllText(ConfigPath));
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.tmp"));

        store.Save(original with { Autostart = false }); // 解放後は保存できる
        Assert.False(NewStore().Load().Settings.Autostart);
    }

    [Fact]
    public void Invalid_settings_are_not_written()
    {
        var store = NewStore();
        store.Load();
        var ex = Assert.Throws<SettingsValidationException>(() => store.Save(AppSettings.Default with { Recognition = new RecognitionSettings("tiny", "ja") }));
        Assert.Contains(ex.Issues, i => i.Path == "recognition.model");
        Assert.False(File.Exists(ConfigPath));
    }

    [Fact]
    public void Save_raises_Changed_with_old_and_new_and_persists_adopted_hotkey()
    {
        var svc = new SettingsService(NewStore());
        var events = new List<(AppSettings Old, AppSettings New)>();
        svc.Changed += (o, n) => events.Add((o, n));
        var store = new SettingsHotkeyStore(svc);

        store.Record(HotkeyFormat.WinJ, []); // 既定と同じなら何も書かない
        Assert.Empty(events);
        Assert.False(File.Exists(ConfigPath));

        var adopted = HotkeyFormat.Create(HotkeyModifiers.Control | HotkeyModifiers.Win, 0x4A);
        store.Record(adopted, []);
        Assert.Single(events);
        Assert.Equal("Ctrl+Win+J", NewStore().Load().Settings.Hotkey.Display);
        store.Record(null, [("Win+J", false, 1459)]); // 全滅なら書かない
        Assert.Single(events);
    }

    // ---- DPAPI ----

    [Fact]
    public void Dpapi_round_trips_for_the_same_user_and_never_stores_plaintext()
    {
        var p = new DpapiSecretProtector();
        const string secret = "sk-test-PLAINTEXT-日本語-123";
        var protectedValue = p.Protect(secret);
        Assert.NotNull(protectedValue);
        Assert.DoesNotContain("PLAINTEXT", protectedValue);
        Assert.True(p.TryUnprotect(protectedValue, out var back));
        Assert.Equal(secret, back);

        var store = NewStore();
        store.Load();
        store.Save(AppSettings.Default with
        {
            Cleanup = AppSettings.Default.Cleanup with { Compatible = new CompatibleCleanupSettings("https://a.example/v1", protectedValue, "m") },
            Proxy = new ProxySettings(ProxyMode.Manual, "http://p.example:80", "u", p.Protect("pw-PLAINTEXT")),
        });
        var text = File.ReadAllText(ConfigPath);
        Assert.DoesNotContain("PLAINTEXT", text);
        Assert.DoesNotContain(secret, text);
        Assert.Contains(protectedValue!, text);
    }

    [Fact]
    public void Empty_secret_becomes_null_and_garbage_or_foreign_ciphertext_fails_without_throwing()
    {
        var p = new DpapiSecretProtector();
        Assert.Null(p.Protect(null));
        Assert.Null(p.Protect(""));
        Assert.True(p.TryUnprotect(null, out var none));
        Assert.Null(none);
        Assert.False(p.TryUnprotect("not base64 !!", out _));
        Assert.False(p.TryUnprotect(Convert.ToBase64String(new byte[64]), out _));

        // entropy（アプリ識別子）が違う暗号文は復号できない。別ユーザーの暗号文と同じく失敗として扱う。
        var other = new DpapiSecretProtector(Encoding.UTF8.GetBytes("someone-else"));
        var foreign = other.Protect("x");
        Assert.False(p.TryUnprotect(foreign, out var leaked));
        Assert.Null(leaked);
    }

    [Fact]
    public void Settings_records_do_not_expose_plaintext_in_ToString()
    {
        var p = new DpapiSecretProtector();
        var s = AppSettings.Default with { Cleanup = AppSettings.Default.Cleanup with { Compatible = new("https://a.example/v1", p.Protect("PLAINTEXT-KEY"), "m") } };
        Assert.DoesNotContain("PLAINTEXT-KEY", s.ToString());
    }

    // ---- URL・ホットキー ----

    [Theory]
    [InlineData("https://api.openai.com/v1", true)]
    [InlineData("https://api.openai.com/v1/", true)]
    [InlineData("http://localhost:8000/v1", true)]
    [InlineData("http://127.0.0.1:8000/v1", true)]
    [InlineData("http://[::1]:8000/v1", true)]
    [InlineData("http://localhost.example/v1", false)]
    [InlineData("http://example.com/v1", false)]
    [InlineData("https://user:pw@example.com/v1", false)]
    [InlineData("https://user@example.com/v1", false)]
    [InlineData("https://@example.com/v1", false)]
    [InlineData("https://example.com/v1?key=1", false)]
    [InlineData("https://example.com/v1#x", false)]
    [InlineData("https://example.com/v1\n", false)]
    [InlineData("https://exa mple.com", false)]
    [InlineData("ftp://example.com", false)]
    [InlineData("example.com/v1", false)]
    [InlineData("", false)]
    public void Compatible_endpoint_url_rules(string url, bool ok)
    {
        Assert.Equal(ok, CompatibleEndpointUrl.Validate(url, out _) is null);
    }

    [Fact]
    public void Base_path_is_preserved_in_the_parsed_uri()
    {
        Assert.Null(CompatibleEndpointUrl.Validate("https://api.openai.com/v1", out var u));
        Assert.Equal("/v1", u!.AbsolutePath);
        Assert.Equal("api.openai.com", CompatibleEndpointUrl.DestinationHost(u));
    }

    [Theory]
    [InlineData("Win", "J", "Win+J")]
    [InlineData("Win,Ctrl", "j", "Ctrl+Win+J")]
    [InlineData("Alt,Ctrl,Shift,Win", "F12", "Ctrl+Alt+Shift+Win+F12")]
    public void Hotkey_is_normalized_in_a_fixed_modifier_order(string mods, string key, string display)
    {
        Assert.True(HotkeyFormat.TryCreate(mods.Split(','), key, out var hk, out _));
        Assert.Equal(display, hk!.Display);
    }

    [Fact]
    public void Candidates_start_with_the_configured_key_without_duplicates()
    {
        var custom = HotkeyFormat.Create(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x4B);
        Assert.Equal(["Ctrl+Shift+K", "Win+J", "Ctrl+Win+J", "Ctrl+Alt+J"], HotkeyNegotiator.CandidatesFor(custom).Select(c => c.Display));
        Assert.Equal(["Ctrl+Win+J", "Win+J", "Ctrl+Alt+J"], HotkeyNegotiator.CandidatesFor(HotkeyNegotiator.DefaultCandidates[1]).Select(c => c.Display));
    }
}
