using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Whisparrot.Core.Hane;

namespace Whisparrot.Infrastructure.Hane;

/// <summary>応答 JSON の寛容な読み取り（未知の任意フィールドは読み飛ばす。設計 §2）。</summary>
internal static partial class HaneJson
{
    [GeneratedRegex(@"^[A-Z][A-Z0-9_]{0,39}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    public static string? Str(JsonObject o, string name) =>
        o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>文字列でも数値でも十進文字列として読む（<c>revision</c> は十進文字列だが、数値で来ても受ける）。</summary>
    public static string? Decimal(JsonObject o, string name)
    {
        if (o[name] is not JsonValue v) return null;
        if (v.TryGetValue<string>(out var s)) return s.Length is > 0 and <= 32 && s.All(char.IsAsciiDigit) ? s : null;
        return v.TryGetValue<long>(out var l) && l >= 0 ? l.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    public static long? Long(JsonObject o, string name) =>
        o[name] is JsonValue v && v.TryGetValue<long>(out var l) ? l : null;

    public static bool Bool(JsonObject o, string name, bool fallback) =>
        o[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

    public static HaneVersion? Version(JsonObject o, string name)
    {
        if (o[name] is not JsonObject v) return null;
        return Long(v, "major") is { } ma && Long(v, "minor") is { } mi && ma is >= 0 and < 1000 && mi is >= 0 and < 1000
            ? new HaneVersion((int)ma, (int)mi) : null;
    }

    /// <summary>エラーコードは固定の識別子形式だけを通す（本文・パス・例外文字列を持ち込ませない）。</summary>
    public static string SafeCode(JsonObject err) =>
        Str(err, "code") is { } c && CodePattern().IsMatch(c) ? c : "UNKNOWN";

    /// <summary><c>effect</c>。<c>not_applied</c> だけが「未適用が確か」。無い・未知の値は不明として扱う。</summary>
    public static HaneEffect Effect(JsonObject err) => Str(err, "effect") == "not_applied" ? HaneEffect.NotApplied : HaneEffect.Unknown;
}
