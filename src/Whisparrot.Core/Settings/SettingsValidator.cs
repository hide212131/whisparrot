namespace Whisparrot.Core.Settings;

/// <summary>読み込み時・保存時に共通で使う意味検証（型検証は JSON 読み取り側）。</summary>
public static class SettingsValidator
{
    public static List<SettingsIssue> Validate(AppSettings s, DateTimeOffset now)
    {
        var issues = new List<SettingsIssue>();
        void Add(string path, string msg) => issues.Add(new SettingsIssue(path, msg));

        if (s.SchemaVersion != AppSettings.CurrentSchemaVersion) Add("schema_version", $"{AppSettings.CurrentSchemaVersion} だけ対応しています");
        if (s.Hotkey.Modifiers == HotkeyModifiers.None || HotkeyFormat.KeyName(s.Hotkey.VirtualKey) is null) Add("hotkey", "ホットキーが不正です");
        if (s.MicrophoneId is { Length: 0 }) Add("microphone_id", "空文字ではなく null を使ってください");
        if (s.MicrophoneId is not null && s.MicrophoneId.Any(char.IsControl)) Add("microphone_id", "制御文字は使えません");
        if (!ModelIds.All.Contains(s.Recognition.Model)) Add("recognition.model", $"未知のモデル: {s.Recognition.Model}");
        if (s.Recognition.Language != AppSettings.Language) Add("recognition.language", $"{AppSettings.Language} だけ対応しています");
        if (s.Cleanup.TimeoutSeconds != AppSettings.CleanupTimeoutSeconds) Add("cleanup.timeout_seconds", $"{AppSettings.CleanupTimeoutSeconds} 固定です");

        var c = s.Cleanup.Compatible;
        if (c.BaseUrl.Length > 0 || s.Cleanup.Provider == CleanupProvider.Compatible)
        {
            if (CompatibleEndpointUrl.Validate(c.BaseUrl, out _) is { } e) Add("cleanup.compatible.base_url", e);
        }
        if (s.Cleanup.Provider == CleanupProvider.Compatible && c.Model.Trim().Length == 0) Add("cleanup.compatible.model", "モデル名が必要です");
        if (c.Model.Any(char.IsControl)) Add("cleanup.compatible.model", "制御文字は使えません");
        if (s.Cleanup.ChatGpt.Model.Any(char.IsControl)) Add("cleanup.chatgpt.model", "制御文字は使えません");
        if (c.ApiKeyDpapi is { Length: 0 }) Add("cleanup.compatible.api_key_dpapi", "空文字ではなく null を使ってください");

        var p = s.Proxy;
        if (p.ManualUrl.Length > 0 || p.Mode == ProxyMode.Manual)
        {
            if (ProxyUrlRules.Validate(p.ManualUrl, allowUserInfo: false) is { } e) Add("proxy.manual_url", e);
        }
        if (p.ManualUsername.Any(char.IsControl)) Add("proxy.manual_username", "制御文字は使えません");
        if (p.ManualPasswordDpapi is { Length: 0 }) Add("proxy.manual_password_dpapi", "空文字ではなく null を使ってください");

        foreach (var (path, t) in new[] { ("updates.last_check_attempt_utc", s.Updates.LastCheckAttemptUtc), ("updates.last_check_success_utc", s.Updates.LastCheckSuccessUtc) })
        {
            if (t is { } v && v > now) Add(path, "将来の時刻です（時計の巻き戻りの可能性）");
        }
        return issues;
    }
}

/// <summary>プロキシ URL の形式規則（設計 §8.1 / §9.1）。HTTP プロキシ（http://host[:port]）だけを受け付ける。</summary>
public static class ProxyUrlRules
{
    public static string? Validate(string text, bool allowUserInfo)
    {
        if (text.Length == 0) return "プロキシ URL が空です";
        if (text.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) return "空白・制御文字は使えません";
        if (!Uri.TryCreate(text, UriKind.Absolute, out var u)) return "http://host:port の形式で指定してください";
        if (u.Scheme != Uri.UriSchemeHttp) return $"{u.Scheme}:// のプロキシには対応していません（http:// のみ。HTTPS プロキシは未検証のため受け付けません）";
        if (u.Host.Length == 0) return "ホストがありません";
        if (!allowUserInfo && (u.UserInfo.Length > 0 || text.Split("://", 2)[1].Split('/')[0].Contains('@')))
            return "URL に認証情報は含められません（ユーザー名・パスワード欄に入力してください）";
        if (text.Contains('?') || text.Contains('#')) return "クエリー・フラグメントは使えません";
        if (u.AbsolutePath != "/") return "パスは指定できません";
        return null;
    }
}
