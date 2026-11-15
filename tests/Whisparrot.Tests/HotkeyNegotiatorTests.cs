using Whisparrot.Core;

namespace Whisparrot.Tests;

public sealed class HotkeyNegotiatorTests
{
    [Fact]
    public void Default_candidates_follow_the_design_order()
    {
        Assert.Equal(["Win+J", "Ctrl+Win+J", "Ctrl+Alt+J"], HotkeyNegotiator.DefaultCandidates.Select(c => c.Display));
    }

    [Fact]
    public void First_success_is_adopted_and_later_candidates_are_not_tried()
    {
        var tried = new List<string>();
        var adopted = HotkeyNegotiator.Negotiate(HotkeyNegotiator.DefaultCandidates, c => { tried.Add(c.Display); return c.Display == "Ctrl+Win+J"; });
        Assert.Equal("Ctrl+Win+J", adopted?.Display);
        Assert.Equal(["Win+J", "Ctrl+Win+J"], tried);
    }

    [Fact]
    public void Win_J_wins_when_it_registers()
    {
        Assert.Equal("Win+J", HotkeyNegotiator.Negotiate(HotkeyNegotiator.DefaultCandidates, _ => true)?.Display);
    }

    [Fact]
    public void All_failures_yield_null_without_throwing()
    {
        Assert.Null(HotkeyNegotiator.Negotiate(HotkeyNegotiator.DefaultCandidates, _ => false));
    }
}

public sealed class HotkeyCandidateOrderTests
{
    [Fact]
    public void Configured_key_leads_then_defaults_without_duplicates()
    {
        var d = HotkeyNegotiator.DefaultCandidates;
        Assert.Equal(["Win+J", "Ctrl+Win+J", "Ctrl+Alt+J"], HotkeyNegotiator.CandidatesFor(d[0]).Select(c => c.Display));
        Assert.Equal(["Ctrl+Win+J", "Win+J", "Ctrl+Alt+J"], HotkeyNegotiator.CandidatesFor(d[1]).Select(c => c.Display));
        var custom = Whisparrot.Core.Settings.HotkeyFormat.Create(HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x4B);
        Assert.Equal(["Alt+Shift+K", "Win+J", "Ctrl+Win+J", "Ctrl+Alt+J"], HotkeyNegotiator.CandidatesFor(custom).Select(c => c.Display));
    }
}
