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
            var recognition = new RecognitionSettings(
                rec.String("model", d.Recognition.Model),
                rec.String("language", d.Recognition.Language));
            rec.Finish();

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
                px.String("manual_url", ""), px.String("manual_username", ""), px.NullableString("manual_password_dpapi", null));
            px.Finish();

            var autostart = r.Bool("autostart", d.Autostart);

            var ins = r.Object("insertion");
            var insertion = new InsertionSettings(ins.Enum("method", d.Insertion.Method, new Dictionary<string, InsertionMethod>
            {
                ["clipboard"] = InsertionMethod.Clipboard, ["sendinput"] = InsertionMethod.SendInput,
            }));
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

    private static Hotkey ReadHotkey(Reader root, Hotkey fallback)
    {
        var h = root.Object("hotkey");
        if (!h.Present) return fallback;
        var mods = h.StringArray("modifiers");
        var key = h.NullableString("key", null);
        h.Finish();
        if (mods is null && key is null) return fallback;
        if (mods is null || key is null)
        {
            root.Issues.Add(new SettingsIssue("hotkey", "modifiers と key は両方必要です"));
            return fallback;
        }
        if (!HotkeyFormat.TryCreate(mods, key, out var hk, out var err))
        {
            root.Issues.Add(new SettingsIssue("hotkey", err!));
            return fallback;
        }
        return hk!;
    }

    public static byte[] Serialize(AppSettings s)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
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
            w.WriteEndObject();
            w.WriteBoolean("autostart", s.Autostart);
            w.WriteStartObject("insertion");
            w.WriteString("method", s.Insertion.Method == InsertionMethod.Clipboard ? "clipboard" : "sendinput");
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
