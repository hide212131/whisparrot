namespace Whisparrot.Core.Settings;

/// <summary>設計 §7.2 の互換エンドポイント URL 検証。HTTPS 必須、HTTP は正確な loopback のみ。</summary>
public static class CompatibleEndpointUrl
{
    public static string? Validate(string text, out Uri? uri)
    {
        uri = null;
        if (text.Length == 0) return "URL が空です";
        if (text.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))) return "URL に空白・制御文字は使えません";
        if (!Uri.TryCreate(text, UriKind.Absolute, out var u)) return "絶対 URL ではありません";
        if (text.Contains('?') || text.Contains('#')) return "クエリー・フラグメントは使えません";

        var schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0) return "絶対 URL ではありません";
        var rest = text[(schemeEnd + 3)..];
        var authority = rest.Split('/')[0];
        if (authority.Contains('@')) return "URL に認証情報（userinfo）を含められません";

        if (u.Scheme == Uri.UriSchemeHttps) { }
        else if (u.Scheme == Uri.UriSchemeHttp)
        {
            if (!IsExactLoopback(u)) return "HTTP は localhost / 127.0.0.1 / ::1 だけ許可されます";
        }
        else return "HTTPS（またはローカルホストの HTTP）だけ使えます";

        if (u.Host.Length == 0) return "ホストがありません";
        uri = u;
        return null;
    }

    /// <summary>正確な loopback 宛（<c>localhost</c> / <c>127.0.0.1</c> / <c>::1</c> の3形だけ。127.0.0.0/8 全体などには広げない）。</summary>
    public static bool IsExactLoopback(Uri uri) => uri.Host is "localhost" or "127.0.0.1" or "[::1]";

    /// <summary>送信先ホストの確認表示用（設計 §7.2: 保存時に送信先を画面で確認できる）。</summary>
    public static string DestinationHost(Uri uri) => uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";
}
