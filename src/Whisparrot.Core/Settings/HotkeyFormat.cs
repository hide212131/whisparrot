namespace Whisparrot.Core.Settings;

/// <summary>config.json の hotkey（修飾キー名の配列 + キー名）と <see cref="Hotkey"/> の相互変換。</summary>
public static class HotkeyFormat
{
    public static Hotkey WinJ { get; } = new(HotkeyModifiers.Win, 0x4A, "Win+J");

    // 正規化順: Ctrl, Alt, Shift, Win
    private static readonly (HotkeyModifiers Flag, string Name)[] ModifierOrder =
    [
        (HotkeyModifiers.Control, "Ctrl"), (HotkeyModifiers.Alt, "Alt"), (HotkeyModifiers.Shift, "Shift"), (HotkeyModifiers.Win, "Win"),
    ];

    private static readonly Dictionary<string, uint> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20, ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["End"] = 0x23, ["Home"] = 0x24,
        ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28, ["Insert"] = 0x2D, ["Delete"] = 0x2E,
    };

    public static IReadOnlyList<string> ModifierNames { get; } = ModifierOrder.Select(m => m.Name).ToArray();

    public static string? KeyName(uint vk)
    {
        if (vk is >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39) return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x6F);
        foreach (var (name, code) in NamedKeys) if (code == vk) return name;
        return null;
    }

    public static bool TryParseKey(string name, out uint vk)
    {
        vk = 0;
        if (name.Length == 0) return false;
        if (name.Length == 1)
        {
            var c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9') { vk = c; return true; }
            return false;
        }
        if ((name[0] is 'F' or 'f') && int.TryParse(name.AsSpan(1), out var n) && n is >= 1 and <= 24 && name.Length <= 3)
        {
            vk = (uint)(0x6F + n);
            return true;
        }
        return NamedKeys.TryGetValue(name, out vk);
    }

    public static string Display(HotkeyModifiers mods, string keyName) =>
        string.Join("+", ModifierOrder.Where(m => mods.HasFlag(m.Flag)).Select(m => m.Name).Append(keyName));

    public static IReadOnlyList<string> ModifierList(HotkeyModifiers mods) =>
        ModifierOrder.Where(m => mods.HasFlag(m.Flag)).Select(m => m.Name).ToArray();

    /// <summary>設計 §8.1 の検証: 修飾キー 1 つ以上・重複なし・単独修飾キー不可・対応キーのみ。</summary>
    public static bool TryCreate(IReadOnlyList<string> modifiers, string key, out Hotkey? hotkey, out string? error)
    {
        hotkey = null;
        var mods = HotkeyModifiers.None;
        foreach (var m in modifiers)
        {
            var hit = ModifierOrder.FirstOrDefault(o => o.Name == m);
            if (hit.Name is null) { error = $"未知の修飾キー: {m}"; return false; }
            if (mods.HasFlag(hit.Flag)) { error = $"修飾キーが重複しています: {m}"; return false; }
            mods |= hit.Flag;
        }
        if (mods == HotkeyModifiers.None) { error = "修飾キーが一つ以上必要です"; return false; }
        if (!TryParseKey(key, out var vk)) { error = $"対応していないキー: {key}"; return false; }
        hotkey = Create(mods, vk);
        error = null;
        return true;
    }

    public static Hotkey Create(HotkeyModifiers mods, uint vk) => new(mods, vk, Display(mods, KeyName(vk) ?? $"0x{vk:X2}"));

    public static bool SameKey(Hotkey a, Hotkey b) => a.Modifiers == b.Modifiers && a.VirtualKey == b.VirtualKey;
}
