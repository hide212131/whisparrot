using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Whisparrot.Core.Settings;
using Whisparrot.Infrastructure.Recognition;
using Whisparrot.Infrastructure.Settings;

namespace Whisparrot.Tests;

/// <summary>クラウド文字起こし設計 §4（T20）: 認識設定の型・parser・serializer・validator と DPAPI 準備契約。</summary>
public sealed class CloudRecognitionSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "whisparrot-cloudcfg-" + Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_dir, "config.json");
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    public CloudRecognitionSettingsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private JsonSettingsStore NewStore() => new(ConfigPath, () => Now);

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static readonly string Cipher = Convert.ToBase64String("not-real-ciphertext"u8.ToArray());

    private static AppSettings WithCloud(RecognitionEngine engine, CloudRecognitionSettings cloud) =>
        AppSettings.Default with { Recognition = AppSettings.Default.Recognition with { Engine = engine, Cloud = cloud } };

    private static string Ser(AppSettings s) => Encoding.UTF8.GetString(SettingsJson.Serialize(s));

    private static HashSet<string> Paths(string json)
    {
        var set = new HashSet<string>();
        using var doc = JsonDocument.Parse(json);
        void Walk(JsonElement e, string prefix)
        {
            if (e.ValueKind != JsonValueKind.Object) return;
            foreach (var p in e.EnumerateObject()) { var path = prefix.Length == 0 ? p.Name : prefix + "." + p.Name; set.Add(path); Walk(p.Value, path); }
        }
        Walk(doc.RootElement, "");
        return set;
    }

    // この版より前（T13 マージ後）の既定設定が書いていたキーの全集合。追加キーが混ざらないことの固定に使う。
    private static readonly string[] LegacyKeys =
    [
        "schema_version", "hotkey", "hotkey.modifiers", "hotkey.key", "microphone_id", "recognition", "recognition.model", "recognition.language",
        "cleanup", "cleanup.provider", "cleanup.timeout_seconds", "cleanup.compatible", "cleanup.compatible.base_url", "cleanup.compatible.api_key_dpapi",
        "cleanup.compatible.model", "cleanup.chatgpt", "cleanup.chatgpt.model", "proxy", "proxy.mode", "proxy.manual_url", "proxy.manual_username",
        "proxy.manual_password_dpapi", "autostart", "insertion", "insertion.method", "updates", "updates.last_check_attempt_utc", "updates.last_check_success_utc",
    ];

    private const string LegacyConfig = """
        {"schema_version":1,"hotkey":{"modifiers":["Win"],"key":"J"},"microphone_id":null,
         "recognition":{"model":"medium","language":"ja"},
         "cleanup":{"provider":"compatible","timeout_seconds":15,"compatible":{"base_url":"https://a.example/v1","api_key_dpapi":null,"model":"m"},"chatgpt":{"model":""}},
         "proxy":{"mode":"environment","manual_url":"","manual_username":"","manual_password_dpapi":null},
         "autostart":false,"insertion":{"method":"clipboard"},
         "updates":{"last_check_attempt_utc":null,"last_check_success_utc":null}}
        """;

    // ---- 既定・旧設定・追加キー非出力 ----

    [Fact]
    public void Defaults_are_local_with_an_empty_cloud_connection()
    {
        var r = AppSettings.Default.Recognition;
        Assert.Equal(RecognitionEngine.Local, r.Engine);
        Assert.Equal(CloudRecognitionSettings.Empty, r.Cloud);
        Assert.True(r.Cloud.IsEmpty);
    }

    [Fact]
    public void Default_serialization_writes_exactly_the_legacy_keys_and_no_additional_default_key()
    {
        Assert.Equal(LegacyKeys.ToHashSet(), Paths(Ser(AppSettings.Default)));
    }

    [Fact]
    public void A_legacy_config_loads_as_local_and_a_normal_save_adds_no_key()
    {
        var r = SettingsJson.Parse(LegacyConfig, Now);
        Assert.Equal(SettingsLoadStatus.Loaded, r.Status);
        Assert.Equal(RecognitionEngine.Local, r.Settings.Recognition.Engine);
        Assert.True(r.Settings.Recognition.Cloud.IsEmpty);
        Assert.Equal("medium", r.Settings.Recognition.Model);
        Assert.Equal(Paths(LegacyConfig), Paths(Ser(r.Settings)));
        Assert.Equal(LegacyKeys.ToHashSet(), Paths(Ser(r.Settings)));
    }

    [Fact]
    public void Store_saves_a_legacy_config_without_new_keys_and_keeps_the_ciphertext_of_the_other_secrets()
    {
        File.WriteAllText(ConfigPath, LegacyConfig);
        var store = NewStore();
        var loaded = store.Load();
        store.Save(loaded.Settings with { Autostart = true });
        Assert.Equal(LegacyKeys.ToHashSet(), Paths(File.ReadAllText(ConfigPath)));
    }

    [Fact]
    public void Only_engine_and_cloud_are_new_keys_for_a_cloud_config_so_an_older_strict_parser_rejects_them_as_unknown()
    {
        // 旧 exe の parser は未知キーを拒否する（SettingsJson.Reader.Finish）。実 exe の旧版での拒否は旧コミットでの実測が別途必要。
        var s = WithCloud(RecognitionEngine.Cloud, new("https://t.example/v1", Cipher, "whisper"));
        var added = Paths(Ser(s)).Except(LegacyKeys).Order().ToArray();
        Assert.Equal(["recognition.cloud", "recognition.cloud.api_key_dpapi", "recognition.cloud.base_url", "recognition.cloud.model", "recognition.engine"], added);
    }

    // ---- round trip / 省略規則 ----

    [Fact]
    public void Cloud_selection_round_trips_through_the_store()
    {
        var s = WithCloud(RecognitionEngine.Cloud, new("https://transcription.example.com/v1", Cipher, "whisper"));
        var store = NewStore();
        store.Load();
        store.Save(s);
        var back = NewStore().Load();
        Assert.Equal(SettingsLoadStatus.Loaded, back.Status);
        Assert.Equal(s, back.Settings);
        Assert.Contains("\"engine\": \"cloud\"", File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void Switching_back_to_local_omits_only_engine_and_keeps_the_cloud_connection()
    {
        var cloud = new CloudRecognitionSettings("https://t.example/v1", Cipher, "whisper");
        var json = Ser(WithCloud(RecognitionEngine.Local, cloud));
        var paths = Paths(json);
        Assert.DoesNotContain("recognition.engine", paths);
        Assert.Contains("recognition.cloud.base_url", paths);
        var back = SettingsJson.Parse(json, Now);
        Assert.Equal(RecognitionEngine.Local, back.Settings.Recognition.Engine);
        Assert.Equal(cloud, back.Settings.Recognition.Cloud);
    }

    [Fact]
    public void Explicit_clear_returns_all_three_to_defaults_and_omits_the_whole_object()
    {
        var cleared = WithCloud(RecognitionEngine.Local, CloudRecognitionSettings.Empty);
        Assert.DoesNotContain("recognition.cloud", Paths(Ser(cleared)));
    }

    [Fact]
    public void Default_children_are_omitted_one_by_one_and_missing_ones_read_back_as_defaults()
    {
        var onlyUrl = Ser(WithCloud(RecognitionEngine.Local, new("https://t.example/v1", null, "")));
        Assert.Equal(["recognition.cloud", "recognition.cloud.base_url"], Paths(onlyUrl).Except(LegacyKeys).Order().ToArray());
        Assert.Equal(new CloudRecognitionSettings("https://t.example/v1", null, ""), SettingsJson.Parse(onlyUrl, Now).Settings.Recognition.Cloud);

        var onlyKey = Ser(WithCloud(RecognitionEngine.Local, new("", Cipher, "")));
        Assert.Equal(["recognition.cloud", "recognition.cloud.api_key_dpapi"], Paths(onlyKey).Except(LegacyKeys).Order().ToArray());
    }

    [Fact]
    public void The_model_is_kept_exactly_as_entered()
    {
        var s = WithCloud(RecognitionEngine.Cloud, new("https://t.example/v1", Cipher, "Azure/Whisper-1 "));
        Assert.Equal("Azure/Whisper-1 ", SettingsJson.Parse(Ser(s), Now).Settings.Recognition.Cloud.Model);
    }

    // ---- 型・enum・null・重複・未知キー ----

    private static SettingsLoadResult ParseRecognition(string recognitionJson) =>
        SettingsJson.Parse($$"""{"schema_version":1,"recognition":{{{recognitionJson}}}}""", Now);

    [Theory]
    [InlineData("\"engine\":\"auto\"")]
    [InlineData("\"engine\":\"Cloud\"")]
    [InlineData("\"engine\":null")]
    [InlineData("\"engine\":1")]
    [InlineData("\"engine\":\"cloud\",\"engine\":\"local\"")]
    [InlineData("\"cloud\":null")]
    [InlineData("\"cloud\":[]")]
    [InlineData("\"cloud\":{\"base_url\":null}")]
    [InlineData("\"cloud\":{\"base_url\":5}")]
    [InlineData("\"cloud\":{\"model\":null}")]
    [InlineData("\"cloud\":{\"model\":false}")]
    [InlineData("\"cloud\":{\"api_key_dpapi\":5}")]
    [InlineData("\"cloud\":{\"api_key_dpapi\":\"\"}")]
    [InlineData("\"cloud\":{\"api_key_dpapi\":\"***not base64***\"}")]
    [InlineData("\"cloud\":{\"unknown\":1}")]
    [InlineData("\"cloud\":{\"base_url\":\"https://a/v1\",\"base_url\":\"https://b/v1\"}")]
    [InlineData("\"cloud\":{},\"cloud\":{}")]
    [InlineData("\"extra\":true")]
    public void Malformed_recognition_keys_are_rejected_not_silently_reset(string recognitionJson)
    {
        var r = ParseRecognition(recognitionJson);
        Assert.Equal(SettingsLoadStatus.Invalid, r.Status);
        Assert.False(r.Usable);
    }

    [Theory]
    [InlineData("\"cloud\":{\"base_url\":\"http://example.com/v1\"}")]        // HTTP は loopback だけ
    [InlineData("\"cloud\":{\"base_url\":\"https://user:pw@example.com/v1\"}")]
    [InlineData("\"cloud\":{\"base_url\":\"https://@example.com/v1\"}")]
    [InlineData("\"cloud\":{\"base_url\":\"https://example.com/v1?x=1\"}")]
    [InlineData("\"cloud\":{\"base_url\":\"https://example.com/v1#f\"}")]
    [InlineData("\"cloud\":{\"base_url\":\"https://exa mple.com/v1\"}")]
    [InlineData("\"cloud\":{\"base_url\":\"not a url\"}")]
    [InlineData("\"cloud\":{\"model\":\"a\\nb\"}")]
    public void Even_when_local_is_selected_a_non_empty_url_syntax_and_model_characters_are_validated(string recognitionJson) =>
        Assert.Equal(SettingsLoadStatus.Invalid, ParseRecognition(recognitionJson).Status);

    [Fact]
    public void Local_selection_does_not_require_url_or_model_but_cloud_selection_does()
    {
        Assert.Equal(SettingsLoadStatus.Loaded, ParseRecognition("\"engine\":\"local\",\"cloud\":{}").Status);
        Assert.Equal(SettingsLoadStatus.Invalid, ParseRecognition("\"engine\":\"cloud\"").Status);
        Assert.Equal(SettingsLoadStatus.Invalid, ParseRecognition("\"engine\":\"cloud\",\"cloud\":{\"base_url\":\"https://t.example/v1\"}").Status);
        Assert.Equal(SettingsLoadStatus.Invalid, ParseRecognition("\"engine\":\"cloud\",\"cloud\":{\"base_url\":\"https://t.example/v1\",\"model\":\"   \"}").Status);
        Assert.Equal(SettingsLoadStatus.Invalid, ParseRecognition("\"engine\":\"cloud\",\"cloud\":{\"model\":\"whisper\"}").Status);
        Assert.Equal(SettingsLoadStatus.Loaded, ParseRecognition("\"engine\":\"cloud\",\"cloud\":{\"base_url\":\"https://t.example/v1\",\"model\":\"whisper\"}").Status);
    }

    [Fact]
    public void Loopback_http_is_accepted_for_the_cloud_url_and_a_missing_key_is_a_readiness_question_not_a_syntax_error()
    {
        var r = ParseRecognition("\"engine\":\"cloud\",\"cloud\":{\"base_url\":\"http://localhost:4000/v1\",\"model\":\"whisper\"}");
        Assert.Equal(SettingsLoadStatus.Loaded, r.Status);
        Assert.Null(r.Settings.Recognition.Cloud.ApiKeyDpapi);
    }

    [Fact]
    public void Existing_local_rules_still_apply()
    {
        Assert.Equal(SettingsLoadStatus.Invalid, ParseRecognition("\"model\":\"tiny\"").Status);
        Assert.Equal(SettingsLoadStatus.Invalid, ParseRecognition("\"language\":\"en\"").Status);
    }

    // ---- 非破壊拒否 ----

    [Fact]
    public void A_config_with_bad_cloud_keys_is_rejected_and_the_file_is_never_touched()
    {
        File.WriteAllText(ConfigPath, """{"schema_version":1,"recognition":{"engine":"quantum","cloud":{"base_url":"http://example.com/v1"}}}""");
        var before = Hash(ConfigPath);
        var store = NewStore();
        var r = store.Load();
        Assert.Equal(SettingsLoadStatus.Invalid, r.Status);
        Assert.False(store.CanSave);
        Assert.Throws<SettingsBlockedException>(() => store.Save(AppSettings.Default));
        Assert.Equal(before, Hash(ConfigPath));
    }

    [Fact]
    public void A_future_schema_with_cloud_keys_is_refused_untouched()
    {
        File.WriteAllText(ConfigPath, """{"schema_version":2,"recognition":{"engine":"cloud","cloud":{"base_url":"https://t.example/v1"}}}""");
        var before = Hash(ConfigPath);
        var store = NewStore();
        Assert.Equal(SettingsLoadStatus.UnsupportedSchema, store.Load().Status);
        Assert.Throws<SettingsBlockedException>(() => store.Save(AppSettings.Default));
        Assert.Equal(before, Hash(ConfigPath));
    }

    [Fact]
    public void Saving_an_invalid_cloud_selection_is_refused_and_the_previous_file_is_kept()
    {
        var store = NewStore();
        store.Load();
        store.Save(AppSettings.Default);
        var before = Hash(ConfigPath);
        Assert.Throws<SettingsValidationException>(() => store.Save(WithCloud(RecognitionEngine.Cloud, CloudRecognitionSettings.Empty)));
        Assert.Throws<SettingsValidationException>(() => store.Save(WithCloud(RecognitionEngine.Local, new("http://example.com/v1", null, ""))));
        Assert.Equal(before, Hash(ConfigPath));
    }

    [Fact]
    public void Existing_ciphertext_survives_unrelated_saves_unchanged()
    {
        var s = WithCloud(RecognitionEngine.Cloud, new("https://t.example/v1", Cipher, "whisper"));
        var svc = new SettingsService(NewStore());
        svc.Save(s);
        svc.Update(x => x with { Autostart = true });
        Assert.Equal(Cipher, NewStore().Load().Settings.Recognition.Cloud.ApiKeyDpapi);
    }

    // ---- 整文設定と混ぜない ----

    [Fact]
    public void Cloud_and_cleanup_connections_are_stored_independently()
    {
        var cleanup = new CleanupSettings(CleanupProvider.Compatible, 15, new CompatibleCleanupSettings("https://cleanup.example/v1", Cipher, "polish"), new ChatGptCleanupSettings(""));
        var s = AppSettings.Default with
        {
            Cleanup = cleanup,
            Recognition = AppSettings.Default.Recognition with
            {
                Engine = RecognitionEngine.Cloud, Cloud = new("https://asr.example/v1", Convert.ToBase64String("other"u8.ToArray()), "whisper"),
            },
        };
        var back = SettingsJson.Parse(Ser(s), Now).Settings;
        Assert.Equal(cleanup, back.Cleanup);
        Assert.Equal("https://asr.example/v1", back.Recognition.Cloud.BaseUrl);
        Assert.NotEqual(back.Cleanup.Compatible.ApiKeyDpapi, back.Recognition.Cloud.ApiKeyDpapi);

        // 認識側を消去しても整文は変わらず、整文を変えても認識は変わらない
        var cleared = back with { Recognition = back.Recognition with { Engine = RecognitionEngine.Local, Cloud = CloudRecognitionSettings.Empty } };
        Assert.Equal(cleanup, SettingsJson.Parse(Ser(cleared), Now).Settings.Cleanup);
        var polishChanged = back with { Cleanup = AppSettings.Default.Cleanup };
        Assert.Equal(back.Recognition, SettingsJson.Parse(Ser(polishChanged), Now).Settings.Recognition);
    }

    [Fact]
    public void Cleanup_keys_are_not_accepted_under_recognition_cloud_and_vice_versa()
    {
        Assert.Equal(SettingsLoadStatus.Invalid, ParseRecognition("\"cloud\":{\"compatible\":{}}").Status);
        Assert.Equal(SettingsLoadStatus.Invalid,
            SettingsJson.Parse("""{"schema_version":1,"cleanup":{"cloud":{}}}""", Now).Status);
    }

    // ---- DPAPI 準備契約 ----

    private sealed class FakeProtector : ISecretProtector
    {
        public Dictionary<string, string?> Map { get; } = [];
        public string? Protect(string? plaintext)
        {
            if (string.IsNullOrEmpty(plaintext)) return null;
            var c = Convert.ToBase64String(Encoding.UTF8.GetBytes("cipher:" + plaintext));
            Map[c] = plaintext;
            return c;
        }
        public bool TryUnprotect(string? protectedBase64, out string? plaintext)
        {
            plaintext = null;
            if (string.IsNullOrEmpty(protectedBase64)) return true;
            if (Map.TryGetValue(protectedBase64, out plaintext)) return true;
            return false;
        }
    }

    private static RecognitionSettings Cloud(string url, string? key, string model, FakeProtector p) =>
        new("small", "ja", RecognitionEngine.Cloud, new(url, p.Protect(key), model));

    [Fact]
    public void Prepare_builds_an_immutable_ready_value_from_a_valid_external_configuration()
    {
        var p = new FakeProtector();
        var r = Assert.IsType<CloudRecognitionConfig.Ready>(CloudRecognitionConfig.Prepare(Cloud("https://transcription.example.com/v1", "DUMMY-KEY", "Whisper", p), p));
        Assert.Equal("https://transcription.example.com/v1/audio/transcriptions", r.Endpoint.ToString());
        Assert.Equal("DUMMY-KEY", r.ApiKey);
        Assert.Equal("Whisper", r.Model);
        Assert.DoesNotContain("DUMMY-KEY", r.ToString());
    }

    [Theory]
    [InlineData("https://transcription.example.com/v1", "https://transcription.example.com/v1/audio/transcriptions")]
    [InlineData("https://transcription.example.com/gateway/v1/", "https://transcription.example.com/gateway/v1/audio/transcriptions")]
    [InlineData("https://transcription.example.com/v1//", "https://transcription.example.com/v1/audio/transcriptions")]
    [InlineData("http://localhost:4000/v1", "http://localhost:4000/v1/audio/transcriptions")]
    public void Endpoint_is_the_base_with_trailing_slashes_trimmed_plus_audio_transcriptions(string baseUrl, string expected)
    {
        var p = new FakeProtector();
        var r = Assert.IsType<CloudRecognitionConfig.Ready>(CloudRecognitionConfig.Prepare(Cloud(baseUrl, "k", "m", p), p));
        Assert.Equal(expected, r.Endpoint.ToString());
    }

    [Fact]
    public void Prepare_does_not_add_or_strip_v1_and_sends_the_model_name_untouched()
    {
        var p = new FakeProtector();
        var r = Assert.IsType<CloudRecognitionConfig.Ready>(CloudRecognitionConfig.Prepare(Cloud("https://t.example", "k", " Azure/whisper ", p), p));
        Assert.Equal("https://t.example/audio/transcriptions", r.Endpoint.ToString());
        Assert.Equal(" Azure/whisper ", r.Model);
    }

    [Fact]
    public void An_external_destination_without_a_key_is_not_ready()
    {
        var p = new FakeProtector();
        var r = Assert.IsType<CloudRecognitionConfig.Unconfigured>(CloudRecognitionConfig.Prepare(Cloud("https://t.example/v1", null, "m", p), p));
        Assert.Contains("API キー", r.Reason);
    }

    [Theory]
    [InlineData("http://localhost:4000/v1")]
    [InlineData("http://127.0.0.1:4000/v1")]
    [InlineData("http://[::1]:4000/v1")]
    [InlineData("https://localhost/v1")]
    public void The_exact_loopback_forms_may_have_an_empty_key_and_then_send_no_authentication(string url)
    {
        var p = new FakeProtector();
        var r = Assert.IsType<CloudRecognitionConfig.Ready>(CloudRecognitionConfig.Prepare(Cloud(url, null, "m", p), p));
        Assert.Null(r.ApiKey);
    }

    [Fact]
    public void A_stored_key_that_cannot_be_decrypted_is_never_downgraded_to_no_auth_even_for_loopback()
    {
        var p = new FakeProtector();
        foreach (var url in new[] { "http://localhost:4000/v1", "https://t.example/v1" })
        {
            var rec = new RecognitionSettings("small", "ja", RecognitionEngine.Cloud, new(url, Cipher, "m")); // Cipher は FakeProtector が復号できない
            var r = Assert.IsType<CloudRecognitionConfig.Unconfigured>(CloudRecognitionConfig.Prepare(rec, p));
            Assert.Contains("復号", r.Reason);
            Assert.DoesNotContain(Cipher, r.Reason);
        }
    }

    [Fact]
    public void A_ciphertext_that_decrypts_to_empty_is_treated_as_undecryptable()
    {
        var p = new FakeProtector();
        p.Map[Cipher] = "";
        var rec = new RecognitionSettings("small", "ja", RecognitionEngine.Cloud, new("http://localhost:1/v1", Cipher, "m"));
        Assert.IsType<CloudRecognitionConfig.Unconfigured>(CloudRecognitionConfig.Prepare(rec, p));
    }

    [Theory]
    [InlineData("key\r\nX-Injected: 1")]
    [InlineData("key\n")]
    [InlineData("ke\ty")]
    [InlineData("key\0")]
    public void A_decrypted_key_with_control_characters_is_not_ready_and_never_reaches_a_header(string key)
    {
        var p = new FakeProtector();
        var r = Assert.IsType<CloudRecognitionConfig.Unconfigured>(CloudRecognitionConfig.Prepare(Cloud("https://t.example/v1", key, "m", p), p));
        Assert.DoesNotContain("Injected", r.Reason);
    }

    [Theory]
    [InlineData("", "m")]
    [InlineData("not a url", "m")]
    [InlineData("http://example.com/v1", "m")]
    [InlineData("https://t.example/v1", "")]
    [InlineData("https://t.example/v1", "   ")]
    [InlineData("https://t.example/v1", "a\nb")]
    public void Missing_or_invalid_url_or_model_is_not_ready(string url, string model)
    {
        var p = new FakeProtector();
        Assert.IsType<CloudRecognitionConfig.Unconfigured>(CloudRecognitionConfig.Prepare(Cloud(url, "k", model, p), p));
    }

    [Fact]
    public void A_local_selection_is_never_ready_for_cloud_even_with_a_complete_connection()
    {
        var p = new FakeProtector();
        var rec = Cloud("https://t.example/v1", "k", "m", p) with { Engine = RecognitionEngine.Local };
        Assert.IsType<CloudRecognitionConfig.Unconfigured>(CloudRecognitionConfig.Prepare(rec, p));
    }

    [Fact]
    public void Unconfigured_reasons_are_fixed_text_that_never_contain_configured_values()
    {
        var p = new FakeProtector();
        var reasons = new[]
        {
            Cloud("", "SECRET-KEY-1", "model-x", p), Cloud("https://secret-host.example/v1", null, "model-x", p),
            Cloud("https://secret-host.example/v1", "SECRET-KEY-1", "", p), Cloud("http://secret-host.example/v1", "SECRET-KEY-1", "m", p),
        }.Select(r => ((CloudRecognitionConfig.Unconfigured)CloudRecognitionConfig.Prepare(r, p)).Reason);
        foreach (var reason in reasons)
        {
            Assert.DoesNotContain("SECRET", reason);
            Assert.DoesNotContain("secret-host", reason);
            Assert.DoesNotContain("model-x", reason);
        }
    }

    [Fact]
    public void Preparation_does_not_mutate_the_settings_so_an_undecryptable_ciphertext_is_kept()
    {
        var p = new FakeProtector();
        var rec = new RecognitionSettings("small", "ja", RecognitionEngine.Cloud, new("https://t.example/v1", Cipher, "m"));
        var copy = rec;
        CloudRecognitionConfig.Prepare(rec, p);
        Assert.Equal(copy, rec);
        Assert.Equal(Cipher, rec.Cloud.ApiKeyDpapi);
    }

    [Fact]
    public void Real_dpapi_round_trips_and_a_different_scope_or_corrupt_ciphertext_is_not_ready_without_deleting_it()
    {
        var dpapi = new DpapiSecretProtector();
        var rec = new RecognitionSettings("small", "ja", RecognitionEngine.Cloud, new("https://t.example/v1", dpapi.Protect("DUMMY-KEY"), "whisper"));
        var ok = Assert.IsType<CloudRecognitionConfig.Ready>(CloudRecognitionConfig.Prepare(rec, dpapi));
        Assert.Equal("DUMMY-KEY", ok.ApiKey);

        // 別ユーザー相当: 復号できない暗号文（entropy が違う実 DPAPI）。壊れた暗号文も同様。
        var other = new DpapiSecretProtector(Encoding.UTF8.GetBytes("another-app"));
        Assert.IsType<CloudRecognitionConfig.Unconfigured>(CloudRecognitionConfig.Prepare(rec, other));
        var corrupt = rec with { Cloud = rec.Cloud with { ApiKeyDpapi = Convert.ToBase64String(new byte[40]) } };
        Assert.IsType<CloudRecognitionConfig.Unconfigured>(CloudRecognitionConfig.Prepare(corrupt, dpapi));
        Assert.NotNull(corrupt.Cloud.ApiKeyDpapi);
    }

    [Fact]
    public void After_a_failed_decrypt_the_user_can_choose_local_and_the_cloud_ciphertext_is_still_saved()
    {
        var undecryptable = WithCloud(RecognitionEngine.Cloud, new("https://t.example/v1", Cipher, "whisper"));
        var svc = new SettingsService(NewStore());
        svc.Save(undecryptable);
        svc.Update(s => s with { Recognition = s.Recognition with { Engine = RecognitionEngine.Local } });
        var back = NewStore().Load().Settings.Recognition;
        Assert.Equal(RecognitionEngine.Local, back.Engine);
        Assert.Equal(Cipher, back.Cloud.ApiKeyDpapi);
        Assert.DoesNotContain("\"engine\"", File.ReadAllText(ConfigPath));
    }
}
