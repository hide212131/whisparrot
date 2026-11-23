using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Whisparrot.Core.Hane;

/// <summary>
/// カーソル近傍の固有名詞・専門用語らしい語の決定的な抽出（設計 §4.2 の近傍部分。文書全体の索引は W-5）。外部 API も辞書も使わない。
/// 候補は、明示された用語（Front Matter）、見出し・リンクの表示名・コードの識別子、カタカナ語・漢字の複合語・英数字混在語や略称。
/// 順位は「明示 → 構造 → 一般の語」の層ごとに、カーソルに近い出現・出現回数・出現位置の順。出力には文書の綴りを保つ。
/// </summary>
public static partial class NearbyTerms
{
    public const int MaxCount = 24;
    public const int MaxChars = 400;
    public const int MaxTermLength = 40;

    private sealed class Candidate(string text, int tier, int distance, int index)
    {
        public string Text = text;
        public int Tier = tier;
        public int Distance = distance;
        public int Index = index;
        public int Count = 1;
    }

    /// <param name="before">カーソル直前の本文（Front Matter・範囲除外後、リンク先を除く前）。</param>
    /// <param name="after">カーソル直後の本文。</param>
    /// <param name="explicitTerms">Front Matter の <c>terms</c> / <c>aliases</c> など明示された表記。</param>
    public static IReadOnlyList<string> Extract(string before, string after, IEnumerable<string>? explicitTerms = null)
    {
        var found = new Dictionary<string, Candidate>(StringComparer.Ordinal);
        var order = 0;

        foreach (var t in explicitTerms ?? [])
            Add(found, t.Trim(), tier: 0, distance: 0, ref order);

        Scan(before, found, ref order, fromEnd: true);
        Scan(after, found, ref order, fromEnd: false);

        var result = new List<string>();
        var chars = 0;
        foreach (var c in found.Values.OrderBy(c => c.Tier).ThenBy(c => c.Distance).ThenByDescending(c => c.Count).ThenBy(c => c.Index))
        {
            if (result.Count >= MaxCount) break;
            if (chars + c.Text.Length > MaxChars) continue; // 長い語が入らなくても、短い語は続けて試す
            result.Add(c.Text);
            chars += c.Text.Length;
        }
        return result;
    }

    private static void Scan(string text, Dictionary<string, Candidate> found, ref int order, bool fromEnd)
    {
        if (text.Length == 0) return;
        int Dist(Match m) => fromEnd ? text.Length - (m.Index + m.Length) : m.Index;

        foreach (Match m in HeadingRx().Matches(text))
        {
            var g = m.Groups[1];
            var s = g.Value.Trim();
            if (s.Length is >= 2 and <= 24 && !HiraganaOnlyRx().IsMatch(s)) Add(found, s, 1, fromEnd ? text.Length - (g.Index + g.Length) : g.Index, ref order);
        }
        foreach (Match m in LinkTextRx().Matches(text))
        {
            var s = m.Groups[1].Value.Trim();
            if (s.Length is >= 2 and <= 30 && !UrlLike(s)) Add(found, s, 1, Dist(m), ref order);
        }
        foreach (Match m in CodeSpanRx().Matches(text))
        {
            var s = m.Groups[1].Value.Trim();
            if (s.Length is >= 2 and <= MaxTermLength && !s.Contains(' ')) Add(found, s, 1, Dist(m), ref order);
        }

        // 一般の語はリンク先の URL を除いた本文から拾う。
        var plain = PolishContextBuilder.StripLinkTargets(text);
        int PlainDist(Match m) => fromEnd ? plain.Length - (m.Index + m.Length) : m.Index;
        foreach (Match m in KatakanaRx().Matches(plain))
            if (m.Value.Any(ch => ch != 'ー')) Add(found, m.Value, 2, PlainDist(m), ref order);
        foreach (Match m in KanjiRx().Matches(plain))
            Add(found, m.Value, 2, PlainDist(m), ref order);
        foreach (Match m in AsciiRx().Matches(plain))
            if (IsAsciiTerm(plain, m)) Add(found, m.Value, 2, PlainDist(m), ref order);
    }

    private static bool IsAsciiTerm(string text, Match m)
    {
        var w = m.Value;
        if (w.Length < 2) return false;
        if (w.Any(char.IsDigit) || w.Contains('_')) return true;                // v2 / gpt4 / snake_case
        if (w.Skip(1).Any(char.IsUpper)) return true;                           // camelCase / 略称
        if (w.Length >= 3 && char.IsUpper(w[0])) return !AtSentenceStart(text, m.Index); // 文中の固有名詞らしい語
        return false;
    }

    private static bool AtSentenceStart(string text, int index)
    {
        for (var i = index - 1; i >= 0; i--)
        {
            var c = text[i];
            if (c is ' ' or '\t' or '　') continue;
            return c is '.' or '!' or '?' or '。' or '！' or '？' or '\n' or '\r' or '#' or '-' or '*' or '>';
        }
        return true;
    }

    private static bool UrlLike(string s) => s.Contains("://", StringComparison.Ordinal);

    private static void Add(Dictionary<string, Candidate> found, string text, int tier, int distance, ref int order)
    {
        if (text.Length == 0 || text.Length > MaxTermLength || text.All(ch => char.IsDigit(ch) || char.IsPunctuation(ch) || char.IsWhiteSpace(ch))) return;
        var key = text.Normalize(NormalizationForm.FormKC).ToLower(CultureInfo.InvariantCulture);
        if (found.TryGetValue(key, out var c))
        {
            c.Count++;
            if (tier < c.Tier) { c.Tier = tier; c.Text = text; c.Distance = distance; }
            else if (tier == c.Tier && distance < c.Distance) { c.Distance = distance; c.Text = text; } // 近い出現の綴りを残す
            return;
        }
        found[key] = new Candidate(text, tier, distance, order++);
    }

    [GeneratedRegex(@"^[ \t]{0,3}#{1,6}[ \t]+(.+?)[ \t#]*$", RegexOptions.Multiline)]
    private static partial Regex HeadingRx();

    [GeneratedRegex(@"(?<!!)\[([^\[\]\r\n]+)\](?:\([^)\r\n]*\)|\[[^\]\r\n]*\])")]
    private static partial Regex LinkTextRx();

    [GeneratedRegex(@"`([^`\r\n]+)`")]
    private static partial Regex CodeSpanRx();

    [GeneratedRegex(@"^[ぁ-ゟー\s]+$")]
    private static partial Regex HiraganaOnlyRx();

    [GeneratedRegex(@"[ァ-ヺヽヾー]{3,}")]
    private static partial Regex KatakanaRx();

    [GeneratedRegex(@"[㐀-䶿一-鿿々〆]{3,}")]
    private static partial Regex KanjiRx();

    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9_]*(?:[.\-][A-Za-z0-9_]+)*")]
    private static partial Regex AsciiRx();
}
