using System.Globalization;
using System.Text;
using System.Text.Json;
using Whisparrot.Core;
using Whisparrot.Core.Settings;

namespace Whisparrot.Infrastructure.Settings;

public enum SettingsLoadStatus
{
    /// <summary>読み込めた。</summary>
    Loaded,
    /// <summary>ファイルが無い。既定値を使う（ファイルはまだ作らない）。</summary>
    Missing,
    /// <summary>JSON として読めない。</summary>
    Corrupt,
    /// <summary>JSON だが型・値・キーが不正。</summary>
    Invalid,
    /// <summary>schema_version が新しすぎる。ファイルは変更しない。</summary>
    UnsupportedSchema,
}

public sealed record SettingsLoadResult(SettingsLoadStatus Status, AppSettings Settings, IReadOnlyList<SettingsIssue> Issues, int? FoundSchemaVersion = null)
{
    public bool Usable => Status is SettingsLoadStatus.Loaded or SettingsLoadStatus.Missing;

    public string Describe() => Status switch
    {
        SettingsLoadStatus.Loaded or SettingsLoadStatus.Missing => "",
        SettingsLoadStatus.UnsupportedSchema => $"config.json の schema_version（{FoundSchemaVersion}）は、この版が対応する {AppSettings.CurrentSchemaVersion} より新しいため読み込みません。ファイルは変更していません。新しい版の whisparrot を使ってください。",
        SettingsLoadStatus.Corrupt => "config.json を JSON として読めません。ファイルは変更していません。" + string.Join(" / ", Issues),
        _ => "config.json に不正な値があります。ファイルは変更していません。" + string.Join(" / ", Issues),
    };
}

/// <summary>設計 §8: JSON を DOM として読み、最初に schema を判定してから型へ変換する。未知のキーは不正。</summary>
public static class SettingsJson
{
    public static SettingsLoadResult Parse(string text, DateTimeOffset now)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        }
        catch (JsonException ex)
        {
            return new(SettingsLoadStatus.Corrupt, AppSettings.Default, [new SettingsIssue("$", $"JSON エラー（行 {ex.LineNumber + 1}）")]);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new(SettingsLoadStatus.Invalid, AppSettings.Default, [new SettingsIssue("$", "オブジェクトではありません")]);

            // schema を最初に判定する。新しすぎるなら他の検証（未知のキー等）は行わない。
            if (!root.TryGetProperty("schema_version", out var sv))
                return new(SettingsLoadStatus.Invalid, AppSettings.Default, [new SettingsIssue("schema_version", "必須です（省略を旧版と推測しません）")]);
            if (sv.ValueKind != JsonValueKind.Number || !sv.TryGetInt32(out var version))
                return new(SettingsLoadStatus.Invalid, AppSettings.Default, [new SettingsIssue("schema_version", "整数ではありません")]);
            if (version > AppSettings.CurrentSchemaVersion)
                return new(SettingsLoadStatus.UnsupportedSchema, AppSettings.Default, [new SettingsIssue("schema_version", $"{version} は新しすぎます")], version);
            if (version < 1)
                return new(SettingsLoadStatus.Invalid, AppSettings.Default, [new SettingsIssue("schema_version", $"{version} は不正です（正の整数）")], version);

            var issues = new List<SettingsIssue>();
            var r = new Reader(root, "", issues);
            var d = AppSettings.Default;

            r.Int("schema_version", version);
            var hotkey = ReadHotkey(r, d.Hotkey);
            var mic = r.NullableString("microphone_id", d.MicrophoneId);

            var rec = r.Object("recognition");
            var recModel = rec.String("model", d.Recognition.Model);
            var recLanguage = rec.String("language", d.Recognition.Language);
            var engine = rec.Enum("engine", d.Recognition.Engine, new Dictionary<string, RecognitionEngine>
            {
                ["local"] = RecognitionEngine.Local, ["cloud"] = RecognitionEngine.Cloud,
            });
            var rc = rec.Object("cloud");
            var cloud = new CloudRecognitionSettings(
                rc.String("base_url", ""), rc.NullableString("api_key_dpapi", null), rc.String("model", ""),
                rc.String("language", CloudRecognitionSettings.DefaultLanguage));
            rc.Finish();
            rec.Finish();
            var recognition = new RecognitionSettings(recModel, recLanguage, engine, cloud);

            var cl = r.Object("cleanup");
            var provider = cl.Enum("provider", d.Cleanup.Provider, new Dictionary<string, CleanupProvider>
            {
                ["off"] = CleanupProvider.Off, ["compatible"] = CleanupProvider.Compatible, ["chatgpt"] = CleanupProvider.ChatGpt,
            });
            var timeout = cl.Int("timeout_seconds", d.Cleanup.TimeoutSeconds);
            var co = cl.Object("compatible");
            var compatible = new CompatibleCleanupSettings(
                co.String("base_url", ""), co.NullableString("api_key_dpapi", null), co.String("model", ""));
            co.Finish();
            var cg = cl.Object("chatgpt");
            var chatgpt = new ChatGptCleanupSettings(cg.String("model", ""));
            cg.Finish();
            cl.Finish();

            var px = r.Object("proxy");
            var proxy = new ProxySettings(
                px.Enum("mode", d.Proxy.Mode, new Dictionary<string, ProxyMode>
                {
                    ["environment"] = ProxyMode.Environment, ["manual"] = ProxyMode.Manual, ["none"] = ProxyMode.None,
                }),
                px.String("manual_url", ""), px.String("manual_username", ""), px.NullableString("manual_password_dpapi", null),
                px.Enum("manual_auth", d.Proxy.ManualAuth, new Dictionary<string, ProxyManualAuth>
                {
                    ["credentials"] = ProxyManualAuth.Credentials, ["windows"] = ProxyManualAuth.Windows,
                }));
            px.Finish();

            var autostart = r.Bool("autostart", d.Autostart);

            var ins = r.Object("insertion");
            var method = ins.Enum("method", d.Insertion.Method, new Dictionary<string, InsertionMethod>
            {
                ["clipboard"] = InsertionMethod.Clipboard, ["sendinput"] = InsertionMethod.SendInput,
            });
            var customKey = ReadKeyCombo(ins, "custom_key");
            var appRoutes = new List<AppRoute>();
            foreach (var item in ins.ObjectArray("app_routes") ?? [])
            {
                var process = item.NullableString("process", null);
                var routeName = item.NullableString("route", null);
                item.Finish();
                if (process is null || routeName is null) { item.Issues.Add(new SettingsIssue(item.Path, "process と route は両方必要です")); continue; }
                if (!InsertionRoutes.TryParse(routeName, out var route))
                { item.Issues.Add(new SettingsIssue(item.Path + ".route", $"未知の値: {routeName}（{string.Join(" / ", InsertionRoutes.AllNames)}）")); continue; }
                appRoutes.Add(new AppRoute(process, route));
            }
            var insertion = new InsertionSettings(method, customKey, appRoutes);
            ins.Finish();

            var up = r.Object("updates");
            var updates = new UpdateSettings(up.Time("last_check_attempt_utc"), up.Time("last_check_success_utc"));
            up.Finish();
            r.Finish();

            if (issues.Count > 0) return new(SettingsLoadStatus.Invalid, AppSettings.Default, issues, version);

            var settings = new AppSettings(version, hotkey, mic, recognition, new CleanupSettings(provider, timeout, compatible, chatgpt),
                proxy, autostart, insertion, updates);
            var semantic = SettingsValidator.Validate(settings, now);
            return semantic.Count > 0
                ? new(SettingsLoadStatus.Invalid, AppSettings.Default, semantic, version)
                : new(SettingsLoadStatus.Loaded, settings, [], version);
        }
    }

    private static Hotkey ReadHotkey(Reader root, Hotkey fallback) => ReadKeyCombo(root, "hotkey") ?? fallback;

    /// <summary>{ "modifiers": [...], "key": "..." } 形式のキー組み合わせ。無ければ null。両方そろわない・不正ならエラーを積んで null。</summary>
    private static Hotkey? ReadKeyCombo(Reader root, string name)
    {
        var h = root.Object(name);
        if (!h.Present) return null;
        var mods = h.StringArray("modifiers");
        var key = h.NullableString("key", null);
        h.Finish();
        if (mods is null && key is null) return null;
        var path = name.Length > 0 && root.Path.Length > 0 ? root.Path + "." + name : name;
        if (mods is null || key is null)
        {
            root.Issues.Add(new SettingsIssue(path, "modifiers と key は両方必要です"));
            return null;
        }
        if (!HotkeyFormat.TryCreate(mods, key, out var hk, out var err))
        {
            root.Issues.Add(new SettingsIssue(path, err!));
            return null;
        }
        return hk;
    }

    public static byte[] Serialize(AppSettings s)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteNumber("schema_version", s.SchemaVersion);
            w.WriteStartObject("hotkey");
            w.WriteStartArray("modifiers");
            foreach (var m in HotkeyFormat.ModifierList(s.Hotkey.Modifiers)) w.WriteStringValue(m);
            w.WriteEndArray();
            w.WriteString("key", HotkeyFormat.KeyName(s.Hotkey.VirtualKey));
            w.WriteEndObject();
            WriteNullable(w, "microphone_id", s.MicrophoneId);
            w.WriteStartObject("recognition");
            w.WriteString("model", s.Recognition.Model);
            w.WriteString("language", s.Recognition.Language);
            // 追加キーは既定のとき書かない: 利用者が選ばない限り、この版が書く config.json に新しいキーを増やさない（旧版が未知キーとして拒否しないため）。
            if (s.Recognition.Engine == RecognitionEngine.Cloud) w.WriteString("engine", "cloud");
            if (!s.Recognition.Cloud.IsEmpty)
            {
                var cloud = s.Recognition.Cloud;
                w.WriteStartObject("cloud");
                if (cloud.BaseUrl.Length > 0) w.WriteString("base_url", cloud.BaseUrl);
                if (cloud.ApiKeyDpapi is not null) w.WriteString("api_key_dpapi", cloud.ApiKeyDpapi);
                if (cloud.Model.Length > 0) w.WriteString("model", cloud.Model);
                if (cloud.Language != CloudRecognitionSettings.DefaultLanguage) w.WriteString("language", cloud.Language); // 空（自動判定）も明示的に残す
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteStartObject("cleanup");
            w.WriteString("provider", s.Cleanup.Provider switch { CleanupProvider.Off => "off", CleanupProvider.Compatible => "compatible", _ => "chatgpt" });
            w.WriteNumber("timeout_seconds", s.Cleanup.TimeoutSeconds);
            w.WriteStartObject("compatible");
            w.WriteString("base_url", s.Cleanup.Compatible.BaseUrl);
            WriteNullable(w, "api_key_dpapi", s.Cleanup.Compatible.ApiKeyDpapi);
            w.WriteString("model", s.Cleanup.Compatible.Model);
            w.WriteEndObject();
            w.WriteStartObject("chatgpt");
            w.WriteString("model", s.Cleanup.ChatGpt.Model);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteStartObject("proxy");
            w.WriteString("mode", s.Proxy.Mode switch { ProxyMode.Environment => "environment", ProxyMode.Manual => "manual", _ => "none" });
            w.WriteString("manual_url", s.Proxy.ManualUrl);
            w.WriteString("manual_username", s.Proxy.ManualUsername);
            WriteNullable(w, "manual_password_dpapi", s.Proxy.ManualPasswordDpapi);
            // 既定（credentials）のときは書かない: 利用者が選ばない限り、この版が書く config.json に新しいキーを増やさない（旧版が未知キーとして拒否しないため）。
            if (s.Proxy.ManualAuth != ProxyManualAuth.Credentials) w.WriteString("manual_auth", "windows");
            w.WriteEndObject();
            w.WriteBoolean("autostart", s.Autostart);
            w.WriteStartObject("insertion");
            w.WriteString("method", s.Insertion.Method == InsertionMethod.Clipboard ? "clipboard" : "sendinput");
            // 追加キーは既定（未設定・空）のとき書かない。
            if (s.Insertion.CustomKey is { } ck)
            {
                w.WriteStartObject("custom_key");
                w.WriteStartArray("modifiers");
                foreach (var m in HotkeyFormat.ModifierList(ck.Modifiers)) w.WriteStringValue(m);
                w.WriteEndArray();
                w.WriteString("key", HotkeyFormat.KeyName(ck.VirtualKey));
                w.WriteEndObject();
            }
            if (s.Insertion.AppRoutes.Count > 0)
            {
                w.WriteStartArray("app_routes");
                foreach (var a in s.Insertion.AppRoutes)
                {
                    w.WriteStartObject();
                    w.WriteString("process", a.Process);
                    w.WriteString("route", InsertionRoutes.Name(a.Route));
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
            w.WriteStartObject("updates");
            WriteNullable(w, "last_check_attempt_utc", FormatTime(s.Updates.LastCheckAttemptUtc));
            WriteNullable(w, "last_check_success_utc", FormatTime(s.Updates.LastCheckSuccessUtc));
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return new UTF8Encoding(false).GetBytes(Encoding.UTF8.GetString(ms.ToArray()) + "\n");
    }

    private static void WriteNullable(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null) w.WriteNull(name); else w.WriteString(name, value);
    }

    internal static string? FormatTime(DateTimeOffset? t) =>
        t?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>型検証つきの読み取り補助。欠損は既定値、null や型違いはパス付きのエラー。</summary>
    private sealed class Reader
    {
        private readonly JsonElement _el;
        private readonly string _path;
        private readonly HashSet<string> _seen = [];

        public List<SettingsIssue> Issues { get; }
        public bool Present { get; }
        public string Path => _path;

        public Reader(JsonElement el, string path, List<SettingsIssue> issues, bool present = true)
        {
            _el = el; _path = path; Issues = issues; Present = present;
            if (present)
            {
                var names = new HashSet<string>();
                foreach (var p in el.EnumerateObject())
                    if (!names.Add(p.Name)) Issues.Add(new SettingsIssue(Join(p.Name), "キーが重複しています"));
            }
        }

        private string Join(string name) => _path.Length == 0 ? name : _path + "." + name;

        private bool TryGet(string name, out JsonElement v)
        {
            v = default;
            if (!Present) return false;
            _seen.Add(name);
            return _el.TryGetProperty(name, out v);
        }

        public Reader Object(string name)
        {
            if (!TryGet(name, out var v)) return new Reader(default, Join(name), Issues, present: false);
            if (v.ValueKind != JsonValueKind.Object)
            {
                Issues.Add(new SettingsIssue(Join(name), v.ValueKind == JsonValueKind.Null ? "null は不正です（欠損とは区別します）" : "オブジェクトではありません"));
                return new Reader(default, Join(name), Issues, present: false);
            }
            return new Reader(v, Join(name), Issues);
        }

        /// <summary>オブジェクトの配列。各要素は添字つきのパス（name[i]）の Reader。配列でない・オブジェクト以外の要素はエラー。</summary>
        public IReadOnlyList<Reader>? ObjectArray(string name)
        {
            if (!TryGet(name, out var v)) return null;
            if (v.ValueKind != JsonValueKind.Array) { Issues.Add(new SettingsIssue(Join(name), "配列ではありません")); return null; }
            var list = new List<Reader>();
            var i = 0;
            foreach (var item in v.EnumerateArray())
            {
                var path = $"{Join(name)}[{i++}]";
                if (item.ValueKind != JsonValueKind.Object) { Issues.Add(new SettingsIssue(path, "オブジェクトではありません")); continue; }
                list.Add(new Reader(item, path, Issues));
            }
            return list;
        }

        public string String(string name, string fallback)
        {
            if (!TryGet(name, out var v)) return fallback;
            if (v.ValueKind == JsonValueKind.String) return v.GetString()!;
            Issues.Add(new SettingsIssue(Join(name), "文字列ではありません"));
            return fallback;
        }

        public string? NullableString(string name, string? fallback)
        {
            if (!TryGet(name, out var v)) return fallback;
            if (v.ValueKind == JsonValueKind.Null) return null;
            if (v.ValueKind == JsonValueKind.String) return v.GetString();
            Issues.Add(new SettingsIssue(Join(name), "文字列または null ではありません"));
            return fallback;
        }

        public bool Bool(string name, bool fallback)
        {
            if (!TryGet(name, out var v)) return fallback;
            if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) return v.GetBoolean();
            Issues.Add(new SettingsIssue(Join(name), "真偽値ではありません"));
            return fallback;
        }

        public int Int(string name, int fallback)
        {
            if (!TryGet(name, out var v)) return fallback;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
            Issues.Add(new SettingsIssue(Join(name), "整数ではありません"));
            return fallback;
        }

        public T Enum<T>(string name, T fallback, Dictionary<string, T> map)
        {
            if (!TryGet(name, out var v)) return fallback;
            if (v.ValueKind == JsonValueKind.String && map.TryGetValue(v.GetString()!, out var e)) return e;
            Issues.Add(new SettingsIssue(Join(name), v.ValueKind == JsonValueKind.String
                ? $"未知の値: {v.GetString()}（{string.Join(" / ", map.Keys)}）" : "文字列ではありません"));
            return fallback;
        }

        public IReadOnlyList<string>? StringArray(string name)
        {
            if (!TryGet(name, out var v)) return null;
            if (v.ValueKind != JsonValueKind.Array) { Issues.Add(new SettingsIssue(Join(name), "配列ではありません")); return null; }
            var list = new List<string>();
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) { Issues.Add(new SettingsIssue(Join(name), "文字列の配列ではありません")); return null; }
                list.Add(item.GetString()!);
            }
            return list;
        }

        public DateTimeOffset? Time(string name)
        {
            if (!TryGet(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
            if (v.ValueKind == JsonValueKind.String
                && v.GetString() is { Length: > 0 } s && s.EndsWith('Z')
                && DateTimeOffset.TryParseExact(s, ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"], CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t))
                return t;
            Issues.Add(new SettingsIssue(Join(name), "RFC3339 UTC（例 2026-01-02T03:04:05Z）または null ではありません"));
            return null;
        }

        public void Finish()
        {
            if (!Present) return;
            foreach (var p in _el.EnumerateObject())
                if (!_seen.Contains(p.Name)) Issues.Add(new SettingsIssue(Join(p.Name), "未知のキーです"));
        }
    }
}
