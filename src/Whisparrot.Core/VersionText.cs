namespace Whisparrot.Core;

/// <summary>画面表示用のバージョン文字列の整形。</summary>
public static class VersionText
{
    /// <summary>
    /// <paramref name="informational"/>（AssemblyInformationalVersion）を優先し、空なら <paramref name="assembly"/> を使う。
    /// <c>+</c> 以降のビルドメタデータは落とし、先頭に <c>v</c> を付ける。どちらも無ければ <c>v0.0.0</c>。
    /// </summary>
    public static string Format(string? informational, Version? assembly)
    {
        var s = informational?.Trim();
        var plus = s?.IndexOf('+') ?? -1;
        if (plus >= 0) s = s![..plus].Trim();
        if (string.IsNullOrEmpty(s)) s = assembly?.ToString(3) ?? "0.0.0";
        if (s.Length > 1 && (s[0] == 'v' || s[0] == 'V') && char.IsDigit(s[1])) s = s[1..];
        return "v" + s;
    }
}
