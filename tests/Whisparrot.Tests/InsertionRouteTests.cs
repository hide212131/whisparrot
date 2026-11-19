using System.Text;
using Whisparrot.App.Windows;
using Whisparrot.Core;
using Whisparrot.Core.Settings;
using Whisparrot.Infrastructure.Settings;
using static Whisparrot.App.Windows.NativeMethods;
using static Whisparrot.App.Windows.TextInjector;

namespace Whisparrot.Tests;

/// <summary>M10: 経路の梯子（auto / paste-message / ctrl-v / shift-insert / typing / custom）とアプリ別上書きの設定・決定ロジック。</summary>
public sealed class InsertionSettingsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private static SettingsLoadResult Parse(string json) => SettingsJson.Parse(json, Now);

    private static string Insertion(string body) => "{\"schema_version\":1,\"insertion\":{" + body + "}}";

    [Fact]
    public void Default_insertion_writes_no_new_keys()
    {
        var text = Encoding.UTF8.GetString(SettingsJson.Serialize(AppSettings.Default));
        Assert.DoesNotContain("custom_key", text);
        Assert.DoesNotContain("app_routes", text);
        Assert.Equal(AppSettings.Default, Parse(text).Settings);
    }

    [Fact]
    public void Custom_key_and_app_routes_round_trip_in_order()
    {
        var s = AppSettings.Default with
        {
            Insertion = new InsertionSettings(InsertionMethod.Clipboard, HotkeyFormat.Create(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x56),
                [new AppRoute("hane", InsertionRoute.Typing), new AppRoute("notepad", InsertionRoute.PasteMessage), new AppRoute("x", InsertionRoute.Custom)]),
        };
        var text = Encoding.UTF8.GetString(SettingsJson.Serialize(s));
        Assert.Contains("\"app_routes\"", text);
        Assert.Contains("\"custom_key\"", text);
        var parsed = Parse(text);
        Assert.Equal(SettingsLoadStatus.Loaded, parsed.Status);
        Assert.Equal(s, parsed.Settings);
        Assert.Equal(["hane", "notepad", "x"], parsed.Settings.Insertion.AppRoutes.Select(a => a.Process));
    }

    [Fact]
    public void Older_files_without_the_new_keys_load_with_no_overrides()
    {
        var r = Parse(Insertion("\"method\":\"clipboard\""));
        Assert.Equal(SettingsLoadStatus.Loaded, r.Status);
        Assert.Empty(r.Settings.Insertion.AppRoutes);
        Assert.Null(r.Settings.Insertion.CustomKey);
    }

    [Theory]
    [InlineData("\"app_routes\":[{\"process\":\"hane\",\"route\":\"bogus\"}]", "insertion.app_routes[0].route")]
    [InlineData("\"app_routes\":[{\"process\":\"hane\"}]", "insertion.app_routes[0]")]
    [InlineData("\"app_routes\":[{\"route\":\"typing\"}]", "insertion.app_routes[0]")]
    [InlineData("\"app_routes\":[{\"process\":\"hane\",\"route\":\"typing\",\"extra\":1}]", "insertion.app_routes[0].extra")]
    [InlineData("\"app_routes\":[\"hane\"]", "insertion.app_routes[0]")]
    [InlineData("\"app_routes\":{}", "insertion.app_routes")]
    [InlineData("\"app_routes\":[{\"process\":\"\",\"route\":\"typing\"}]", "insertion.app_routes[0].process")]
    [InlineData("\"app_routes\":[{\"process\":\"a\\\\b\",\"route\":\"typing\"}]", "insertion.app_routes[0].process")]
    [InlineData("\"app_routes\":[{\"process\":\"a\",\"route\":\"typing\"},{\"process\":\"A.exe\",\"route\":\"ctrl-v\"}]", "insertion.app_routes[1].process")]
    [InlineData("\"app_routes\":[{\"process\":\"a\",\"route\":\"custom\"}]", "insertion.app_routes[0].route")]
    [InlineData("\"custom_key\":{\"modifiers\":[],\"key\":\"V\"}", "insertion.custom_key")]
    [InlineData("\"custom_key\":{\"modifiers\":[\"Ctrl\"]}", "insertion.custom_key")]
    [InlineData("\"custom_key\":{\"modifiers\":[\"Ctrl\"],\"key\":\"?\"}", "insertion.custom_key")]
    [InlineData("\"custom_key\":{\"modifiers\":[\"Hyper\"],\"key\":\"V\"}", "insertion.custom_key")]
    [InlineData("\"custom_key\":null", "insertion.custom_key")]
    public void Invalid_insertion_values_are_rejected_with_the_path(string body, string path)
    {
        var r = Parse(Insertion(body));
        Assert.Equal(SettingsLoadStatus.Invalid, r.Status);
        Assert.Contains(r.Issues, i => i.Path == path);
    }

    [Fact]
    public void Route_names_are_the_documented_six()
    {
        Assert.Equal(["auto", "paste-message", "ctrl-v", "shift-insert", "typing", "custom"], InsertionRoutes.AllNames);
        foreach (var n in InsertionRoutes.AllNames)
        {
            Assert.True(InsertionRoutes.TryParse(n, out var r));
            Assert.Equal(n, InsertionRoutes.Name(r));
        }
        Assert.False(InsertionRoutes.TryParse("send-input", out _));
        Assert.False(InsertionRoutes.TryParse(null, out _));
    }

    [Theory]
    [InlineData("hane", "hane")]
    [InlineData("HANE", "hane")]
    [InlineData("Hane.exe", "hane")]
    [InlineData("  hane.EXE ", "hane")]
    public void Override_lookup_ignores_case_extension_and_whitespace(string asked, string stored)
    {
        var ins = new InsertionSettings(InsertionMethod.Clipboard, null, [new AppRoute(stored, InsertionRoute.Typing)]);
        Assert.Equal(InsertionRoute.Typing, ins.OverrideFor(asked));
    }

    [Theory]
    [InlineData("notepad")]
    [InlineData("")]
    [InlineData(null)]
    public void Override_lookup_returns_null_when_unlisted(string? asked) =>
        Assert.Null(new InsertionSettings(InsertionMethod.Clipboard, null, [new AppRoute("hane", InsertionRoute.Typing)]).OverrideFor(asked));

    [Fact]
    public void Insertion_settings_compare_by_value_including_the_list()
    {
        var a = new InsertionSettings(InsertionMethod.Clipboard, null, [new AppRoute("x", InsertionRoute.Typing)]);
        var b = new InsertionSettings(InsertionMethod.Clipboard, null, [new AppRoute("x", InsertionRoute.Typing)]);
        Assert.Equal(a, b);
        Assert.NotEqual(a, b with { AppRoutes = [new AppRoute("x", InsertionRoute.CtrlV)] });
        Assert.Equal(new InsertionSettings(InsertionMethod.Clipboard), new InsertionSettings(InsertionMethod.Clipboard, null, []));
    }

    [Theory]
    [InlineData("Ctrl+Alt+V", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x56)]
    [InlineData("Ctrl-Alt-V", HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x56)]
    [InlineData("ctrl-shift-insert", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x2D)]
    [InlineData(" Alt + F9 ", HotkeyModifiers.Alt, 0x78)]
    [InlineData("Win+Space", HotkeyModifiers.Win, 0x20)]
    public void Key_text_parses_with_plus_or_minus(string text, HotkeyModifiers mods, int vk)
    {
        Assert.True(HotkeyFormat.TryParse(text, out var hk, out var err), err);
        Assert.Equal((mods, (uint)vk), (hk!.Modifiers, hk.VirtualKey));
    }

    [Theory]
    [InlineData("")]
    [InlineData("V")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl++V")]
    [InlineData("Ctrl+Ctrl+V")]
    [InlineData("Hyper+V")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+?")]
    public void Invalid_key_text_is_refused(string text)
    {
        Assert.False(HotkeyFormat.TryParse(text, out var hk, out var err));
        Assert.Null(hk);
        Assert.False(string.IsNullOrEmpty(err));
    }

    [Theory]
    [InlineData("hane", true)]
    [InlineData("My App 2", true)]
    [InlineData("", false)]
    [InlineData("a/b", false)]
    [InlineData("a:b", false)]
    [InlineData("a\tb", false)]
    public void Process_names_are_validated(string name, bool ok) => Assert.Equal(ok, InsertionRules.ValidateProcess(name) is null);

    [Fact]
    public void Process_name_length_is_capped() => Assert.NotNull(InsertionRules.ValidateProcess(new string('a', InsertionRules.MaxProcessLength + 1)));
}

public sealed class RouteDecisionTests
{
    private static readonly InsertionSettings Overrides = new(InsertionMethod.Clipboard, null,
        [new AppRoute("hane", InsertionRoute.Typing), new AppRoute("notepad", InsertionRoute.ShiftInsert)]);

    [Fact]
    public void An_app_override_wins_over_the_class_judgement() =>
        Assert.Equal(PasteStrategy.Typing, DecideStrategy(PasteStrategy.Auto, Overrides, "Hane"));

    [Fact]
    public void Unlisted_apps_and_unknown_processes_stay_on_auto()
    {
        Assert.Equal(PasteStrategy.Auto, DecideStrategy(PasteStrategy.Auto, Overrides, "code"));
        Assert.Equal(PasteStrategy.Auto, DecideStrategy(PasteStrategy.Auto, Overrides, null));
        Assert.Equal(PasteStrategy.Auto, DecideStrategy(PasteStrategy.Auto, new InsertionSettings(InsertionMethod.Clipboard), "hane"));
    }

    [Fact]
    public void A_forced_strategy_from_diagnostics_wins_over_the_override() =>
        Assert.Equal(PasteStrategy.PasteMessage, DecideStrategy(PasteStrategy.PasteMessage, Overrides, "hane"));

    [Theory]
    [InlineData(InsertionRoute.Auto, PasteStrategy.Auto)]
    [InlineData(InsertionRoute.PasteMessage, PasteStrategy.PasteMessage)]
    [InlineData(InsertionRoute.CtrlV, PasteStrategy.CtrlV)]
    [InlineData(InsertionRoute.ShiftInsert, PasteStrategy.ShiftInsert)]
    [InlineData(InsertionRoute.Typing, PasteStrategy.Typing)]
    [InlineData(InsertionRoute.Custom, PasteStrategy.Custom)]
    internal void Every_route_maps_to_a_strategy(InsertionRoute route, PasteStrategy expected) => Assert.Equal(expected, FromRoute(route));

    [Theory]
    [InlineData("paste-message", PasteStrategy.PasteMessage)]
    [InlineData("ctrl-v", PasteStrategy.CtrlV)]
    [InlineData("send-input", PasteStrategy.CtrlV)]
    [InlineData("shift-insert", PasteStrategy.ShiftInsert)]
    [InlineData("typing", PasteStrategy.Typing)]
    [InlineData("custom", PasteStrategy.Custom)]
    internal void Diagnostic_strategy_names_parse(string name, PasteStrategy expected)
    {
        Assert.True(TryParseStrategy(name, out var got));
        Assert.Equal(expected, got);
    }

    [Theory]
    [InlineData(PasteStrategy.ShiftInsert, Routes.ShiftInsert)]
    [InlineData(PasteStrategy.Custom, Routes.Custom)]
    [InlineData(PasteStrategy.CtrlV, Routes.CtrlV)]
    internal void Key_routes_send_keys_only_and_report_their_own_route(PasteStrategy strategy, string route)
    {
        var msgs = 0;
        var keys = 0;
        var r = PasteStep(strategy, () => { msgs++; return MessageResult.Delivered; }, () => { keys++; return true; });
        Assert.Equal(new PasteSend(true, route), r);
        Assert.Equal((0, 1), (msgs, keys));
    }

    [Fact]
    public void A_standard_class_with_auto_still_prefers_the_message()
    {
        var keys = 0;
        var r = PasteStep(PasteStrategy.Auto, () => MessageResult.Delivered, () => { keys++; return true; }, null, TargetKind.StandardEdit);
        Assert.Equal(new PasteSend(true, Routes.PasteMessage), r);
        Assert.Equal(0, keys);
    }
}

public sealed class KeyInputTests
{
    private static string Describe(INPUT i)
    {
        var up = (i.u.ki.dwFlags & KEYEVENTF_KEYUP) != 0 ? "up" : "down";
        var ext = (i.u.ki.dwFlags & KEYEVENTF_EXTENDEDKEY) != 0 ? ":ext" : "";
        return $"{up}:{i.u.ki.wVk:X2}{ext}";
    }

    [Fact]
    public void Ctrl_alt_v_presses_modifiers_then_the_key_then_releases_in_reverse() =>
        Assert.Equal(["down:11", "down:12", "down:56", "up:56", "up:12", "up:11"],
            BuildChordInputs(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x56).Select(Describe));

    [Fact]
    public void Shift_insert_marks_insert_as_an_extended_key() =>
        Assert.Equal(["down:10", "down:2D:ext", "up:2D:ext", "up:10"], BuildChordInputs(HotkeyModifiers.Shift, 0x2D).Select(Describe));

    [Fact]
    public void Win_is_sent_as_the_left_windows_key() =>
        Assert.Equal(["down:5B", "down:20", "up:20", "up:5B"], BuildChordInputs(HotkeyModifiers.Win, 0x20).Select(Describe));

    [Fact]
    public void Ctrl_v_matches_what_the_old_sender_sent() =>
        Assert.Equal(["down:11", "down:56", "up:56", "up:11"], BuildChordInputs(HotkeyModifiers.Control, 0x56).Select(Describe));
}

public sealed class TypingInputTests
{
    [Theory]
    [InlineData("a\nb", "a\nb")]
    [InlineData("a\r\nb", "a\nb")]
    [InlineData("a\rb", "a\nb")]
    [InlineData("a\n\nb", "a\n\nb")]
    [InlineData("a\r\n\r\nb", "a\n\nb")]
    [InlineData("a\tb", "a\tb")]
    [InlineData("a\u0007b\u001bc\0d", "abcd")]
    [InlineData("こんにちは、世界。", "こんにちは、世界。")]
    public void Text_is_normalized_to_lf_newlines_and_printable_characters(string input, string expected) =>
        Assert.Equal(expected, NormalizeTypingText(input));

    [Fact]
    public void Each_unit_is_a_unicode_down_up_pair_with_no_virtual_key()
    {
        var inputs = BuildTypingInputs("あ");
        Assert.Equal(2, inputs.Length);
        Assert.All(inputs, i => { Assert.Equal(INPUT_KEYBOARD, i.type); Assert.Equal(0, i.u.ki.wVk); Assert.Equal('あ', (char)i.u.ki.wScan); Assert.NotEqual(0u, i.u.ki.dwFlags & KEYEVENTF_UNICODE); });
        Assert.Equal(0u, inputs[0].u.ki.dwFlags & KEYEVENTF_KEYUP);
        Assert.NotEqual(0u, inputs[1].u.ki.dwFlags & KEYEVENTF_KEYUP);
    }

    [Fact]
    public void A_newline_is_sent_as_the_lf_character_and_never_as_the_enter_key()
    {
        var inputs = BuildTypingInputs(NormalizeTypingText("一行目\r\n二行目"));
        Assert.Contains(inputs, i => i.u.ki.wScan == 0x0A);
        Assert.DoesNotContain(inputs, i => i.u.ki.wScan == 0x0D);
        Assert.DoesNotContain(inputs, i => i.u.ki.wVk != 0); // VK_RETURN（0x0D）を含め、仮想キーは一切送らない
    }

    [Fact]
    public void A_surrogate_pair_is_sent_as_two_consecutive_units()
    {
        var inputs = BuildTypingInputs("😀");
        Assert.Equal(4, inputs.Length);
        Assert.Equal(["D83D", "D83D", "DE00", "DE00"], inputs.Select(i => i.u.ki.wScan.ToString("X4")));
    }

    [Fact]
    public void Chunks_cover_the_text_exactly_and_never_split_a_surrogate_pair()
    {
        var text = new string('a', TypingChunkUnits - 1) + "😀" + new string('b', 700);
        var chunks = TypingChunks(text).ToList();
        Assert.Equal(text, string.Concat(chunks.Select(c => text.Substring(c.Start, c.Length))));
        Assert.All(chunks, c => Assert.False(char.IsHighSurrogate(text[c.Start + c.Length - 1]) && c.Start + c.Length < text.Length));
        Assert.Equal(TypingChunkUnits + 1, chunks[0].Length);
    }

    [Fact]
    public void A_long_text_goes_out_in_a_few_batches_not_one_call_per_character()
    {
        var text = new string('あ', 5000);
        Assert.InRange(TypingChunks(text).Count(), 2, 40);
        Assert.Empty(TypingChunks(""));
    }
}

[Collection("Clipboard")]
public sealed class RecordedTargetTests
{
    private static PasteTarget Target(IntPtr fg, IntPtr focus, IntPtr target, uint pid, bool hosted = false) =>
        new(fg, focus, target, "Cls", focus == IntPtr.Zero ? "" : "Focus", "Edit", "proc", hosted, pid);

    [Fact]
    public void A_destroyed_window_is_no_longer_a_target()
    {
        var recorded = new RecordedTarget(Target(new IntPtr(0x0BADBAD), IntPtr.Zero, new IntPtr(0x0BADBAD), 1234));
        Assert.Null(ResolveRecorded(recorded));
    }

    [Fact]
    public void A_live_window_of_another_process_than_recorded_is_rejected()
    {
        // ハンドルの再利用: 同じ hwnd が別プロセスのウィンドウになっていたら、記録した対象ではない。
        using var host = new PasteMessageTests.EditHost();
        var recorded = new RecordedTarget(Target(host.Hwnd, host.Hwnd, host.Hwnd, (uint)Environment.ProcessId + 1));
        Assert.Null(ResolveRecorded(recorded));
    }

    [Fact]
    public void A_live_window_keeps_its_recorded_control()
    {
        using var host = new PasteMessageTests.EditHost();
        var recorded = new RecordedTarget(Target(host.Hwnd, host.Hwnd, host.Hwnd, (uint)Environment.ProcessId));
        Assert.Equal(host.Hwnd, ResolveRecorded(recorded)!.Value.Target);
    }

    [Fact]
    public void A_vanished_control_falls_back_to_its_window()
    {
        using var host = new PasteMessageTests.EditHost();
        var recorded = new RecordedTarget(Target(host.Hwnd, new IntPtr(0x0BADBAD), new IntPtr(0x0BADBAD), (uint)Environment.ProcessId));
        var r = ResolveRecorded(recorded)!.Value;
        Assert.Equal(host.Hwnd, r.Target);
        Assert.Equal(host.Hwnd, r.Foreground);
    }

    [Fact]
    public void A_window_destroyed_after_recording_is_no_longer_a_target()
    {
        var host = new PasteMessageTests.EditHost();
        var recorded = new RecordedTarget(Target(host.Hwnd, host.Hwnd, host.Hwnd, (uint)Environment.ProcessId));
        Assert.NotNull(ResolveRecorded(recorded));
        host.Dispose();
        Assert.Null(ResolveRecorded(recorded));
    }

    [Fact]
    public async Task Injecting_into_a_gone_window_fails_without_sending_or_touching_the_clipboard()
    {
        var before = Win32.ReadUnicodeText();
        var recorded = new RecordedTarget(Target(new IntPtr(0x0BADBAD), IntPtr.Zero, new IntPtr(0x0BADBAD), 1234));
        var r = await new TextInjector().InjectAsync("本文", recorded, CancellationToken.None);
        Assert.Equal(InjectOutcome.Failed, r.Outcome);
        Assert.Equal("入力先のウィンドウが見つかりません", r.Message);
        Assert.Equal(before, Win32.ReadUnicodeText());
    }
}
