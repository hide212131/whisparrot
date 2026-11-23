using System.Globalization;

namespace Whisparrot.Core.Update;

/// <summary>セマンティックバージョン 2.0（major.minor.patch[-prerelease][+build]）。先頭の <c>v</c> は許す。ビルドメタデータは比較に使わない。</summary>
public sealed class SemVer : IComparable<SemVer>, IEquatable<SemVer>
{
    private readonly string[] _pre;

    private SemVer(int major, int minor, int patch, string[] pre)
    {
        Major = major; Minor = minor; Patch = patch; _pre = pre;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public bool IsPrerelease => _pre.Length > 0;

    public static bool TryParse(string? text, out SemVer version)
    {
        version = null!;
        var s = text?.Trim();
        if (string.IsNullOrEmpty(s)) return false;
        if (s[0] is 'v' or 'V') s = s[1..];
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        var dash = s.IndexOf('-');
        var core = dash >= 0 ? s[..dash] : s;
        var pre = dash >= 0 ? s[(dash + 1)..] : null;

        var parts = core.Split('.');
        if (parts.Length != 3) return false;
        var nums = new int[3];
        for (var i = 0; i < 3; i++)
            if (!TryNumber(parts[i], out nums[i])) return false;

        var ids = Array.Empty<string>();
        if (pre is not null)
        {
            ids = pre.Split('.');
            foreach (var id in ids)
            {
                if (id.Length == 0 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')) return false;
                if (id.All(char.IsAsciiDigit) && id.Length > 1 && id[0] == '0') return false; // 数字だけの識別子に先頭ゼロは不可
            }
        }
        version = new SemVer(nums[0], nums[1], nums[2], ids);
        return true;
    }

    private static bool TryNumber(string s, out int n)
    {
        n = 0;
        if (s.Length == 0 || !s.All(char.IsAsciiDigit) || (s.Length > 1 && s[0] == '0')) return false;
        return int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out n);
    }

    /// <summary>SemVer 2.0 §11: コアを数値比較し、プレリリース無しはありより大きい。識別子は左から比べ、数字のみ＜英字を含む、数字同士は数値、英字は ASCII 順、共通部分が同じなら長い方が大きい。</summary>
    public int CompareTo(SemVer? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        if (_pre.Length == 0 || other._pre.Length == 0) return other._pre.Length.CompareTo(_pre.Length);

        for (var i = 0; i < Math.Min(_pre.Length, other._pre.Length); i++)
        {
            var a = _pre[i];
            var b = other._pre[i];
            var an = a.All(char.IsAsciiDigit);
            var bn = b.All(char.IsAsciiDigit);
            if (an && bn)
            {
                // 桁あふれを避けるため、先頭ゼロ無しの数字列は長さ→辞書順で比べる。
                c = a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
            }
            else if (an != bn) c = an ? -1 : 1;
            else c = string.CompareOrdinal(a, b);
            if (c != 0) return Math.Sign(c);
        }
        return _pre.Length.CompareTo(other._pre.Length);
    }

    public bool Equals(SemVer? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is SemVer v && Equals(v);

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, string.Join('.', _pre));

    public override string ToString() => $"{Major}.{Minor}.{Patch}" + (_pre.Length > 0 ? "-" + string.Join('.', _pre) : "");

    public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;
    public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;
    public static bool operator >=(SemVer a, SemVer b) => a.CompareTo(b) >= 0;
    public static bool operator <=(SemVer a, SemVer b) => a.CompareTo(b) <= 0;
}
