using System.Text;
using System.Text.RegularExpressions;

namespace Whisparrot.Core.Hane;

/// <summary>
/// Hane から取得した文脈を、整文リクエストに載せる形へ整える（設計 §4.1 の上限と優先順位）。
/// 直前 1,200・直後 200 スカラー値、タイトル 120、メタデータはタイトルと合わせて 200、用語は 24 件・400 文字（<see cref="NearbyTerms"/>）。
/// 合計の最大は 2,000 文字で、設計の 2,500 を超えない（要約は W-5 で加えるときに合計を再検査する）。
/// 外部送信の前に、先頭の Front Matter の除去、整文確定待ちの範囲の除外、Markdown のリンク先の除去を行う。生の Front Matter は送らない。
/// </summary>
public static partial class PolishContextBuilder
{
    public const int BeforeLimit = 1200;
    public const int AfterLimit = 200;
    public const int TitleLimit = 120;
    public const int MetadataLimit = 200; // タイトルを含む
    private const int MetadataValueLimit = 60;
    private const int MetadataItemsLimit = 12;

    /// <returns>送れる文脈。すべて空なら null（従来の発話だけの整文に倒す）。</returns>
    public static PolishContext? Build(HaneContext c)
    {
        var before = c.Before;
        var after = c.After;
        var pendingUnsafe = false;

        if (c.PendingRanges.Count > 0)
        {
            // 追跡できない範囲があれば、その発話では当該文書の抜粋と用語を省く（設計 §4.1）。
            if (c.PendingRanges.Any(r => !r.Tracked)) pendingUnsafe = true;
            else
            {
                before = RemoveRanges(before, c.BeforeStartByte, c.PendingRanges, ref pendingUnsafe);
                after = RemoveRanges(after, c.AfterStartByte, c.PendingRanges, ref pendingUnsafe);
            }
        }
        if (pendingUnsafe) { before = ""; after = ""; }

        // 先頭の Front Matter は文書の先頭から始まる切片にだけ入り得る（途中の `---` は水平線）。
        if (c.BeforeStartByte is null or 0) before = StripLeadingFrontMatter(before);
        if (c.AfterStartByte is null or 0) after = StripLeadingFrontMatter(after);

        var beforeForTerms = before;
        var afterForTerms = after;
        before = TailScalars(StripLinkTargets(before), BeforeLimit);
        after = HeadScalars(StripLinkTargets(after), AfterLimit);

        var title = c.IsUntitled ? "" : HeadScalars(Collapse(c.Title), TitleLimit);
        var fields = c.FrontMatterStatus == "present" && c.FrontMatter is { } fm ? FrontMatterFields.Parse(fm) : FrontMatterFields.Empty;
        if (title.Length == 0 && fields.Title is { } ft && !c.IsUntitled) title = HeadScalars(ft, TitleLimit);

        var metadata = BuildMetadata(fields, title);
        var terms = pendingUnsafe ? [] : NearbyTerms.Extract(beforeForTerms, afterForTerms, fields.Terms.Concat(fields.Aliases));

        var ctx = new PolishContext(before, after, title, metadata, terms);
        return ctx.IsEmpty ? null : ctx;
    }

    // ── 整文確定待ちの範囲の除外 ──

    /// <summary>整文待ちの範囲（UTF-8 バイトの半開区間）を切片から取り除き、前後を連結する。切片の開始位置が分からなければ使えない。</summary>
    private static string RemoveRanges(string text, long? start, IReadOnlyList<HanePendingRange> ranges, ref bool unsafeRange)
    {
        if (text.Length == 0) return text;
        var bytes = Encoding.UTF8.GetBytes(text);
        if (start is null) { unsafeRange = true; return ""; }
        var end = start.Value + bytes.Length;
        var drop = new bool[bytes.Length];
        var any = false;
        foreach (var r in ranges)
        {
            var from = Math.Max(r.StartByte, start.Value);
            var to = Math.Min(r.EndByte, end);
            for (var i = from; i < to; i++) { drop[i - start.Value] = true; any = true; }
        }
        if (!any) return text;
        var kept = new List<byte>(bytes.Length);
        for (var i = 0; i < bytes.Length; i++) if (!drop[i]) kept.Add(bytes[i]);
        return Encoding.UTF8.GetString(kept.ToArray());
    }

    // ── Front Matter ──

    /// <summary>先頭が Front Matter の開始行なら、閉じ行までを除く。閉じ行を安全に判定できない場合は、その切片を外部送信しない（空にする）。</summary>
    public static string StripLeadingFrontMatter(string text)
    {
        var s = text.StartsWith('﻿') ? text[1..] : text;
        var first = LineEnd(s, 0);
        if (s[..first].TrimEnd() != "---") return text;
        var pos = NextLine(s, first);
        while (pos < s.Length)
        {
            var end = LineEnd(s, pos);
            var line = s[pos..end].TrimEnd();
            if (line is "---" or "...") return s[NextLine(s, end)..];
            pos = NextLine(s, end);
        }
        return "";
    }

    private static int LineEnd(string s, int from)
    {
        var i = s.IndexOfAny(['\r', '\n'], from);
        return i < 0 ? s.Length : i;
    }

    private static int NextLine(string s, int lineEnd)
    {
        if (lineEnd >= s.Length) return s.Length;
        if (s[lineEnd] == '\r' && lineEnd + 1 < s.Length && s[lineEnd + 1] == '\n') return lineEnd + 2;
        return lineEnd + 1;
    }

    private static IReadOnlyList<string> BuildMetadata(FrontMatterFields f, string title)
    {
        var budget = MetadataLimit - title.Length;
        var list = new List<string>();
        void Add(string key, IReadOnlyList<string> values)
        {
            if (values.Count == 0 || budget <= 0) return;
            var line = $"{key}: {string.Join(", ", values)}";
            if (line.Length > budget) line = TailTrim(line, budget);
            if (line.Length <= key.Length + 2) return;
            list.Add(line);
            budget -= line.Length;
        }
        if (f.Title is { } t && t != title) Add("title", [t]);
        Add("aliases", f.Aliases);
        Add("tags", f.Tags);
        Add("terms", f.Terms);
        return list;
    }

    private static string TailTrim(string s, int max) => HeadScalars(s, max).TrimEnd(',', ' ');

    /// <summary>許可リスト（title / aliases / tags / terms）だけを読む。YAML は評価せず、タグ・アンカー・エイリアス・ブロックスカラー・マップは採らない。</summary>
    private sealed partial record FrontMatterFields(string? Title, IReadOnlyList<string> Aliases, IReadOnlyList<string> Tags, IReadOnlyList<string> Terms)
    {
        public static FrontMatterFields Empty { get; } = new(null, [], [], []);

        public static FrontMatterFields Parse(string inner)
        {
            var lines = inner.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            string? title = null;
            List<string> aliases = [], tags = [], terms = [];
            for (var i = 0; i < lines.Length; i++)
            {
                var m = KeyRx().Match(lines[i]);
                if (!m.Success) continue;
                var key = m.Groups[1].Value.ToLowerInvariant();
                if (key is not ("title" or "aliases" or "tags" or "terms")) continue;
                var value = StripComment(m.Groups[2].Value.Trim());
                var items = new List<string>();
                if (value.Length == 0)
                {
                    for (var j = i + 1; j < lines.Length; j++)
                    {
                        var item = ListItemRx().Match(lines[j]);
                        if (item.Success) { AddItem(items, StripComment(item.Groups[1].Value.Trim())); continue; }
                        if (lines[j].Trim().Length == 0 || lines[j].TrimStart().StartsWith('#')) continue;
                        break;
                    }
                }
                else if (value.StartsWith('[') && value.EndsWith(']'))
                {
                    foreach (var part in value[1..^1].Split(',')) AddItem(items, part.Trim());
                }
                else AddItem(items, value);

                switch (key)
                {
                    case "title": title ??= items.FirstOrDefault(); break;
                    case "aliases": aliases.AddRange(items); break;
                    case "tags": tags.AddRange(items); break;
                    case "terms": terms.AddRange(items); break;
                }
            }
            return new FrontMatterFields(title, Cap(aliases), Cap(tags), Cap(terms));
        }

        private static List<string> Cap(List<string> l) => l.Distinct(StringComparer.Ordinal).Take(MetadataItemsLimit).ToList();

        private static void AddItem(List<string> items, string raw)
        {
            if (raw.Length == 0 || "&*!|>{[%@`".Contains(raw[0])) return; // アンカー・エイリアス・タグ・ブロックスカラー・マップ・入れ子は読まない
            var v = raw;
            if (v.Length >= 2 && ((v[0] == '"' && v[^1] == '"') || (v[0] == '\'' && v[^1] == '\''))) v = v[1..^1];
            v = Collapse(v);
            if (v.Length > 0) items.Add(HeadScalars(v, MetadataValueLimit));
        }

        private static string StripComment(string v)
        {
            if (v.StartsWith('"') || v.StartsWith('\'')) return v;
            var i = v.IndexOf(" #", StringComparison.Ordinal);
            return (i < 0 ? v : v[..i]).TrimEnd();
        }

        [GeneratedRegex(@"^([A-Za-z_][\w\-]*)[ \t]*:(?:[ \t]+(.*)|[ \t]*)$")]
        private static partial Regex KeyRx();

        [GeneratedRegex(@"^[ \t]*-[ \t]+(.*)$")]
        private static partial Regex ListItemRx();
    }

    // ── Markdown ──

    /// <summary>Markdown のリンク先を除き、表示テキストだけを残す（画像は代替テキスト）。リンク先の定義行も除く。相対参照先は展開しない。</summary>
    public static string StripLinkTargets(string text)
    {
        if (text.Length == 0) return text;
        text = LinkDefinitionRx().Replace(text, "");
        text = InlineLinkRx().Replace(text, "$1");
        return ReferenceLinkRx().Replace(text, "$1");
    }

    [GeneratedRegex(@"^[ \t]{0,3}\[[^\]\r\n]+\]:[ \t]*\S.*(\r?\n|$)", RegexOptions.Multiline)]
    private static partial Regex LinkDefinitionRx();

    [GeneratedRegex(@"!?\[([^\[\]]*)\]\([^)\r\n]*\)")]
    private static partial Regex InlineLinkRx();

    [GeneratedRegex(@"!?\[([^\[\]\r\n]+)\]\[[^\]\r\n]*\]")]
    private static partial Regex ReferenceLinkRx();

    // ── 文字量（Unicode スカラー値。サロゲートペアの途中で切らない）──

    private static string Collapse(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    /// <summary>末尾（カーソル側）から <paramref name="max"/> スカラー値。途中の行から始まる場合は、半分以上残るなら最初の改行までを捨てて行の連続性を保つ。</summary>
    public static string TailScalars(string s, int max)
    {
        if (CountScalars(s) <= max) return s;
        var start = s.Length;
        for (var n = 0; n < max && start > 0; n++)
        {
            start--;
            if (char.IsLowSurrogate(s[start]) && start > 0 && char.IsHighSurrogate(s[start - 1])) start--;
        }
        var cut = s[start..];
        var atLineStart = start > 0 && (s[start - 1] == '\n');
        if (!atLineStart)
        {
            var nl = cut.IndexOf('\n');
            if (nl >= 0 && CountScalars(cut[(nl + 1)..]) >= max / 2) cut = cut[(nl + 1)..];
        }
        return cut;
    }

    public static string HeadScalars(string s, int max)
    {
        if (s.Length <= max) return s; // UTF-16 長が上限以下ならスカラー値数も上限以下
        var end = 0;
        for (var n = 0; n < max && end < s.Length; n++)
        {
            end += char.IsHighSurrogate(s[end]) && end + 1 < s.Length && char.IsLowSurrogate(s[end + 1]) ? 2 : 1;
        }
        return s[..end];
    }

    private static int CountScalars(string s)
    {
        var n = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
            n++;
        }
        return n;
    }
}
