using Whisparrot.Core;
using Whisparrot.Core.Hane;

namespace Whisparrot.Tests.Hane;

/// <summary>文脈の組み立て（上限・優先順位・外部送信前の除去）と近傍の用語抽出（W-3 / 設計 §4.1・§4.2）。</summary>
public sealed class PolishContextTests
{
    private static HaneContext Ctx(string before = "", string after = "", string title = "メモ.md", string fmStatus = "absent", string? fm = null,
        long? beforeStart = 100, long? afterStart = 200, long cursor = 200, bool untitled = false, params HanePendingRange[] pending) =>
        new("inst", "doc", "1", cursor, new HaneSelection(cursor, cursor), title, untitled, fmStatus, fm, before, after, false, false, beforeStart, afterStart, pending);

    // ---- 上限 ----

    [Fact]
    public void Before_keeps_the_cursor_side_up_to_1200_scalars_and_after_the_first_200()
    {
        var before = string.Concat(Enumerable.Range(0, 400).Select(i => $"行{i:D3}の文章です。\n"));
        var after = new string('あ', 500);
        var r = PolishContextBuilder.Build(Ctx(before, after))!;

        Assert.True(r.Before.Length <= 1200);
        Assert.EndsWith("行399の文章です。\n", r.Before);   // カーソル直前は残る
        Assert.StartsWith("行", r.Before);                    // 行の途中から始まらない
        Assert.Equal(200, r.After.Length);
    }

    [Fact]
    public void Truncation_never_splits_a_surrogate_pair()
    {
        var before = string.Concat(Enumerable.Repeat("😀", 1500)); // 1,500 スカラー値 = 3,000 UTF-16 単位
        var after = string.Concat(Enumerable.Repeat("😀", 300));
        var r = PolishContextBuilder.Build(Ctx(before, after))!;
        Assert.Equal(1200 * 2, r.Before.Length);
        Assert.Equal(200 * 2, r.After.Length);
        Assert.All(r.Before.Where(char.IsSurrogate).Select((c, i) => (c, i)), t => Assert.Equal(t.i % 2 == 0, char.IsHighSurrogate(t.c)));
    }

    [Fact]
    public void Title_is_capped_at_120_and_metadata_with_the_title_at_200()
    {
        var fm = "title: x\naliases: [" + string.Join(", ", Enumerable.Range(0, 12).Select(i => new string((char)('a' + i), 30))) + "]\ntags:\n  - " + new string('t', 60) + "\nterms: [" + new string('u', 60) + "]";
        var r = PolishContextBuilder.Build(Ctx("本文", title: new string('題', 300), fmStatus: "present", fm: fm))!;
        Assert.Equal(120, r.Title.Length);
        Assert.True(r.Title.Length + r.Metadata.Sum(m => m.Length) <= 200);
    }

    [Fact]
    public void The_whole_context_stays_within_the_designed_total()
    {
        var text = string.Concat(Enumerable.Range(0, 800).Select(i => $"サンプル{i}号機とGpt{i}を使う。"));
        var fm = "terms: [" + string.Join(", ", Enumerable.Range(0, 30).Select(i => "用語" + i)) + "]";
        var r = PolishContextBuilder.Build(Ctx(text, text, new string('題', 200), "present", fm))!;
        var total = r.Before.Length + r.After.Length + r.Title.Length + r.Metadata.Sum(m => m.Length) + r.Terms.Sum(t => t.Length);
        Assert.True(total <= 2500, total.ToString());
        Assert.True(r.Terms.Count <= 24);
    }

    [Fact]
    public void An_entirely_empty_context_is_null_so_the_legacy_request_is_used()
    {
        Assert.Null(PolishContextBuilder.Build(Ctx(title: "", untitled: true)));
    }

    [Fact]
    public void An_untitled_document_does_not_send_a_title()
    {
        var r = PolishContextBuilder.Build(Ctx("本文です。", title: "無題", untitled: true))!;
        Assert.Equal("", r.Title);
    }

    // ---- Front Matter ----

    [Fact]
    public void A_leading_front_matter_in_the_excerpt_is_removed_before_sending()
    {
        var before = "---\ntitle: 秘密の計画\nsecret: abc\n---\n本文の一行目。\n";
        var r = PolishContextBuilder.Build(Ctx(before, beforeStart: 0))!;
        Assert.Equal("本文の一行目。\n", r.Before);
        Assert.DoesNotContain("秘密", r.Before);
    }

    [Fact]
    public void An_unclosed_front_matter_start_drops_that_excerpt()
    {
        var r = PolishContextBuilder.Build(Ctx("---\ntitle: 途中\nkey: v", beforeStart: 0, afterStart: 5, after: "追記"))!;
        Assert.Equal("", r.Before);
        Assert.Equal("追記", r.After);
    }

    [Fact]
    public void A_horizontal_rule_in_the_middle_of_a_document_is_not_front_matter()
    {
        var before = "---\n水平線のあとの文。\n";
        var r = PolishContextBuilder.Build(Ctx(before, beforeStart: 500))!;
        Assert.Equal(before, r.Before);
    }

    [Fact]
    public void Only_the_allow_listed_keys_are_taken_from_front_matter_and_raw_front_matter_is_never_sent()
    {
        var fm = "title: 製品計画\npassword: hunter2\nauthor: someone\naliases:\n  - プラン\n  - \"Plan B\"\ntags: [企画, &anchor x, 'レビュー']\nterms: *ref\nextra: {a: 1}";
        var r = PolishContextBuilder.Build(Ctx("本文。", title: "計画.md", fmStatus: "present", fm: fm))!;
        var all = string.Join("\n", r.Metadata) + r.Title + r.Before + string.Join(",", r.Terms);
        Assert.DoesNotContain("hunter2", all);
        Assert.DoesNotContain("someone", all);
        Assert.DoesNotContain("password", all);
        Assert.Contains("aliases: プラン, Plan B", r.Metadata);
        Assert.Contains("tags: 企画, レビュー", r.Metadata);            // アンカー付きは読まない
        Assert.DoesNotContain(r.Metadata, m => m.StartsWith("terms"));   // エイリアス（*ref）は展開しない
        Assert.Contains("title: 製品計画", r.Metadata);                   // 表示名と違う title は補助として載せる
        Assert.Contains("プラン", r.Terms);                               // aliases / terms は用語の明示候補
    }

    // ---- Markdown のリンク先 ----

    [Fact]
    public void Link_targets_are_removed_and_the_display_text_is_kept()
    {
        var before = "詳細は[設計書](../secret/design.md)と![図](img/a.png)、[参照][r1]を見る。\n[r1]: https://internal.example/x\n続き。";
        var r = PolishContextBuilder.Build(Ctx(before))!;
        Assert.Equal("詳細は設計書と図、参照を見る。\n続き。", r.Before);
        Assert.DoesNotContain("secret", r.Before);
        Assert.DoesNotContain("internal", r.Before);
    }

    // ---- 整文確定待ちの範囲 ----

    [Fact]
    public void A_pending_inserted_range_is_cut_out_of_the_before_excerpt_and_both_sides_are_joined()
    {
        // before は [100, 200) のバイト範囲。ASCII なので文字数 = バイト数。[130,150) が整文待ちの挿入範囲。
        var before = new string('a', 30) + new string('P', 20) + new string('b', 50);
        var r = PolishContextBuilder.Build(Ctx(before, pending: new HanePendingRange("h", true, 130, 150)))!;
        Assert.Equal(new string('a', 30) + new string('b', 50), r.Before);
        Assert.DoesNotContain("P", r.Before);
    }

    [Fact]
    public void A_pending_range_after_the_cursor_is_cut_out_of_the_after_excerpt()
    {
        var after = new string('c', 10) + new string('P', 5) + new string('d', 20);
        var r = PolishContextBuilder.Build(Ctx(after: after, pending: new HanePendingRange("h", true, 210, 215)))!;
        Assert.Equal(new string('c', 10) + new string('d', 20), r.After);
    }

    [Fact]
    public void An_untracked_pending_range_omits_the_excerpts_and_terms_for_that_utterance()
    {
        var r = PolishContextBuilder.Build(Ctx("東京タワーの話。", "続き", pending: new HanePendingRange("h", false, 0, 0)))!;
        Assert.Equal("", r.Before);
        Assert.Equal("", r.After);
        Assert.Empty(r.Terms);
        Assert.Equal("メモ.md", r.Title); // タイトルは本文の抜粋ではない
    }

    [Fact]
    public void A_pending_range_just_before_the_cursor_keeps_the_text_before_it_and_multibyte_ranges_use_utf8_offsets()
    {
        // 「前の文。」(UTF-8 12 バイト) + 整文待ちの「原文です」(12 バイト) がカーソル直前。
        var r = PolishContextBuilder.Build(Ctx("前の文。原文です", beforeStart: 176, cursor: 200, pending: new HanePendingRange("h", true, 188, 200)))!;
        Assert.Equal("前の文。", r.Before);
    }

    [Fact]
    public void A_pending_range_covering_the_whole_before_leaves_nothing_of_it()
    {
        var r = PolishContextBuilder.Build(Ctx("PPPPP", pending: new HanePendingRange("h", true, 90, 300)))!;
        Assert.Equal("", r.Before);
    }

    // ---- 用語抽出 ----

    [Fact]
    public void Terms_pick_katakana_kanji_compounds_and_mixed_ascii_but_not_plain_words()
    {
        var terms = NearbyTerms.Extract("今日はクラウドストレージの機械学習モデルについて、gpt4 と OAuth と camelCase を使う。the plan is simple。", "");
        Assert.Contains("クラウドストレージ", terms);
        Assert.Contains("機械学習", terms);
        Assert.Contains("モデル", terms);
        Assert.Contains("gpt4", terms);
        Assert.Contains("OAuth", terms);
        Assert.Contains("camelCase", terms);
        Assert.DoesNotContain("plan", terms);
        Assert.DoesNotContain("the", terms);
        Assert.DoesNotContain("今日", terms); // 二文字の漢字語は拾わない
    }

    [Fact]
    public void Terms_include_headings_link_names_and_code_identifiers_and_ignore_urls()
    {
        var terms = NearbyTerms.Extract("## 概要設計\n[Hane 連携](https://example.com/some_path)を `SessionCoordinator` から呼ぶ。", "");
        Assert.Contains("概要設計", terms);
        Assert.Contains("Hane 連携", terms);
        Assert.Contains("SessionCoordinator", terms);
        Assert.DoesNotContain(terms, t => t.Contains("example") || t.Contains("some_path"));
    }

    [Fact]
    public void Terms_are_ranked_explicit_first_then_nearest_to_the_cursor_and_keep_the_documents_spelling()
    {
        var before = "ヴァイオリン工房の話。遠い話題のデータベース。近い話題のサーバー";
        var after = "続きのキャッシュ";
        var terms = NearbyTerms.Extract(before, after, ["明示語", "SERVER"]);
        Assert.Equal("明示語", terms[0]);
        Assert.Equal("SERVER", terms[1]);
        Assert.True(terms.ToList().IndexOf("サーバー") < terms.ToList().IndexOf("データベース"));
        Assert.True(terms.ToList().IndexOf("データベース") < terms.ToList().IndexOf("ヴァイオリン"));
    }

    [Fact]
    public void Terms_are_capped_at_24_items_and_400_chars_and_are_deterministic()
    {
        var text = string.Join("、", Enumerable.Range(0, 100).Select(i => "サンプルワード" + new string((char)('ア' + i % 40), 3) + i));
        var a = NearbyTerms.Extract(text, text);
        var b = NearbyTerms.Extract(text, text);
        Assert.Equal(a, b);
        Assert.True(a.Count <= 24);
        Assert.True(a.Sum(t => t.Length) <= 400);

        var longTerms = Enumerable.Range(0, 24).Select(i => new string((char)('あ' + i), 30)).ToArray();
        Assert.True(NearbyTerms.Extract("", "", longTerms).Sum(t => t.Length) <= 400);
    }

    [Fact]
    public void Duplicates_differing_only_by_case_or_width_are_merged()
    {
        var terms = NearbyTerms.Extract("OAuth と OAUTH と oAuth", "");
        Assert.Single(terms);
    }

    [Fact]
    public void Terms_are_empty_for_plain_hiragana_text()
    {
        Assert.Empty(NearbyTerms.Extract("きょうはいいてんきですね", "あしたもはれるでしょう"));
    }

    [Fact]
    public void Ascii_sentence_starts_are_not_mistaken_for_proper_nouns()
    {
        var terms = NearbyTerms.Extract("This works. Then we ship Windows builds.", "");
        Assert.DoesNotContain("This", terms);
        Assert.DoesNotContain("Then", terms);
        Assert.Contains("Windows", terms);
    }
}
