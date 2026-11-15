namespace Whisparrot.Core;

[Flags]
public enum HotkeyModifiers { None = 0, Alt = 1, Control = 2, Shift = 4, Win = 8 }

public sealed record Hotkey(HotkeyModifiers Modifiers, uint VirtualKey, string Display);

public static class HotkeyNegotiator
{
    /// <summary>設計 §5 の既定候補順。D-4 の実機確認が済むまで暫定。</summary>
    public static IReadOnlyList<Hotkey> DefaultCandidates { get; } =
    [
        new(HotkeyModifiers.Win, 0x4A, "Win+J"),
        new(HotkeyModifiers.Control | HotkeyModifiers.Win, 0x4A, "Ctrl+Win+J"),
        new(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x4A, "Ctrl+Alt+J"),
    ];

    /// <summary>
    /// 設定のホットキーを先頭に、既定候補を続ける（重複は除く）。設計 §5 の交渉順。
    /// 交渉で採用されたキーは設定へ保存しない（一時的な登録失敗が次回以降の先頭候補として居座らないように）。
    /// </summary>
    public static IReadOnlyList<Hotkey> CandidatesFor(Hotkey configured) =>
        [configured, .. DefaultCandidates.Where(c => c.Modifiers != configured.Modifiers || c.VirtualKey != configured.VirtualKey)];

    /// <summary>候補を順に試し、登録に成功した最初のものを採用する。全滅なら null。</summary>
    public static Hotkey? Negotiate(IEnumerable<Hotkey> candidates, Func<Hotkey, bool> tryRegister)
    {
        foreach (var c in candidates)
        {
            if (tryRegister(c)) return c;
        }
        return null;
    }
}
