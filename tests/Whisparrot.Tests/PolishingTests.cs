using System.Diagnostics;
using Whisparrot.Core;

namespace Whisparrot.Tests;

/// <summary>設計 §3.3 の整文（Polishing）状態と、「発話を失わない」原則（原文をちょうど一度だけ投入）の試験。</summary>
public sealed class PolishingTests : IDisposable
{
    private readonly FakeRecorder _rec = new();
    private readonly FakeRecognizer _stt = new() { Result = new RecognitionResult("えーと明日は３時にあの会議です", "cpu") };
    private readonly FakeInjector _inj = new();
    private readonly FakePolisher _pol = new();
    private readonly SessionCoordinator _c;
    private readonly List<SessionState> _states = [];
    private readonly List<SessionNotice> _notices = [];

    public PolishingTests()
    {
        _c = new SessionCoordinator(_rec, _stt, _inj, _pol);
        _c.StateChanged += s => { lock (_states) _states.Add(s.State); };
        _c.Noticed += n => { lock (_notices) _notices.Add(n); };
    }

    public void Dispose() => _c.Dispose();

    private Task InState(SessionState s) => Wait.UntilAsync(() => _c.Snapshot.State == s, $"state {s} (now {_c.Snapshot.State})");

    private async Task RecordAndStop()
    {
        _c.Toggle(); await InState(SessionState.Recording);
        _c.Toggle();
    }

    [Fact]
    public async Task Success_injects_the_polished_text_once_and_never_the_raw_text()
    {
        _pol.Setup = new PolishSetup.Ready((raw, _) => Task.FromResult(PolishResult.Success("明日は3時に会議です。")), TimeSpan.FromSeconds(15));
        await RecordAndStop();
        await InState(SessionState.Idle);

        Assert.Equal(["明日は3時に会議です。"], _inj.Texts);
        Assert.Equal([SessionState.Recording, SessionState.Recognizing, SessionState.Polishing, SessionState.Inserting, SessionState.Idle], _states);
        Assert.Empty(_notices);
    }

    [Fact]
    public async Task The_polisher_receives_the_exact_recognized_text()
    {
        string? seen = null;
        _pol.Setup = new PolishSetup.Ready((raw, _) => { seen = raw; return Task.FromResult(PolishResult.Success("x")); }, TimeSpan.FromSeconds(15));
        await RecordAndStop();
        await InState(SessionState.Idle);
        Assert.Equal("えーと明日は３時にあの会議です", seen);
    }

    [Theory]
    [InlineData(PolishFailure.Network)]
    [InlineData(PolishFailure.Timeout)]
    [InlineData(PolishFailure.Http)]
    [InlineData(PolishFailure.Auth)]
    [InlineData(PolishFailure.Empty)]
    [InlineData(PolishFailure.Malformed)]
    [InlineData(PolishFailure.Incomplete)]
    [InlineData(PolishFailure.TooLarge)]
    [InlineData(PolishFailure.Error)]
    public async Task Every_fallback_kind_injects_the_raw_text_exactly_once_with_a_warning(PolishFailure kind)
    {
        _pol.Setup = new PolishSetup.Ready((_, _) => Task.FromResult(PolishResult.Fallback(kind, "HTTP 500")), TimeSpan.FromSeconds(15));
        await RecordAndStop();
        await InState(SessionState.Idle);

        Assert.Equal(["えーと明日は３時にあの会議です"], _inj.Texts);
        var n = Assert.Single(_notices);
        Assert.Equal(NoticeKind.Warning, n.Kind);
        Assert.Contains("整文できなかったため、そのまま入力しました", n.Message);
    }

    [Fact]
    public async Task A_success_with_blank_text_is_not_trusted_and_falls_back_to_raw()
    {
        _pol.Setup = new PolishSetup.Ready((_, _) => Task.FromResult(PolishResult.Success("  \n ")), TimeSpan.FromSeconds(15));
        await RecordAndStop();
        await InState(SessionState.Idle);
        Assert.Equal(["えーと明日は３時にあの会議です"], _inj.Texts);
    }

    [Fact]
    public async Task A_polisher_exception_falls_back_to_raw_once()
    {
        _pol.Setup = new PolishSetup.Ready((_, _) => throw new InvalidOperationException("boom"), TimeSpan.FromSeconds(15));
        await RecordAndStop();
        await InState(SessionState.Idle);
        Assert.Equal(["えーと明日は３時にあの会議です"], _inj.Texts);
        Assert.DoesNotContain("boom", _notices.Single().Message); // 例外メッセージは表示に出さない
    }

    [Fact]
    public async Task A_polisher_that_never_returns_is_cut_at_the_deadline_and_a_late_result_is_ignored()
    {
        var never = new TaskCompletionSource<PolishResult>();
        _pol.Setup = new PolishSetup.Ready((_, _) => never.Task, TimeSpan.FromMilliseconds(300)); // 取消を無視する実装
        var sw = Stopwatch.StartNew();
        await RecordAndStop();
        await InState(SessionState.Idle);
        sw.Stop();

        Assert.Equal(["えーと明日は３時にあの会議です"], _inj.Texts);
        Assert.InRange(sw.ElapsedMilliseconds, 250, 3000);
        Assert.Contains("時間切れ", _notices.Single().Message);

        never.SetResult(PolishResult.Success("遅れて成功"));
        await Task.Delay(100);
        await _c.FlushAsync();
        Assert.Single(_inj.Texts); // 二重投入しない
    }

    [Fact]
    public async Task Cancel_while_polishing_injects_nothing_and_drops_a_late_completion()
    {
        var gate = new TaskCompletionSource<PolishResult>();
        CancellationToken token = default;
        _pol.Setup = new PolishSetup.Ready((_, ct) => { token = ct; return gate.Task; }, TimeSpan.FromSeconds(15));
        await RecordAndStop();
        await InState(SessionState.Polishing);
        await Wait.UntilAsync(() => token.CanBeCanceled, "polisher started");

        _c.Cancel();
        await InState(SessionState.Idle);
        Assert.True(token.IsCancellationRequested);
        Assert.Empty(_inj.Texts); // 取消は原文も投入しない（設計 §7.1 / M1 の取消と一致）

        gate.SetResult(PolishResult.Success("遅れて届いた整文"));
        await Task.Delay(100);
        await _c.FlushAsync();
        Assert.Empty(_inj.Texts);
        Assert.Equal(SessionState.Idle, _c.Snapshot.State);
        Assert.Empty(_notices);
    }

    [Fact]
    public async Task A_new_session_after_a_polishing_cancel_is_not_confused_by_the_old_completion()
    {
        var gate = new TaskCompletionSource<PolishResult>();
        _pol.Setup = new PolishSetup.Ready((_, _) => gate.Task, TimeSpan.FromSeconds(15));
        await RecordAndStop();
        await InState(SessionState.Polishing);
        _c.Cancel();
        await InState(SessionState.Idle);

        _pol.Setup = new PolishSetup.Ready((_, _) => Task.FromResult(PolishResult.Success("新しい整文")), TimeSpan.FromSeconds(15));
        await RecordAndStop();
        await InState(SessionState.Idle);
        gate.SetResult(PolishResult.Success("古い整文"));
        await Task.Delay(100);
        await _c.FlushAsync();
        Assert.Equal(["新しい整文"], _inj.Texts);
    }

    [Fact]
    public async Task Hotkey_during_polishing_does_not_start_a_new_recording()
    {
        var gate = new TaskCompletionSource<PolishResult>();
        _pol.Setup = new PolishSetup.Ready((_, _) => gate.Task, TimeSpan.FromSeconds(15));
        await RecordAndStop();
        await InState(SessionState.Polishing);
        _c.Toggle();
        await _c.FlushAsync();
        Assert.Equal(SessionState.Polishing, _c.Snapshot.State);
        Assert.Equal(1, _rec.StartCount);
        gate.SetResult(PolishResult.Success("ok"));
        await InState(SessionState.Idle);
    }

    [Fact]
    public async Task Unconfigured_skips_polishing_injects_raw_once_and_says_why()
    {
        var called = 0;
        _pol.Setup = new PolishSetup.Unconfigured("ベース URL が未設定です");
        await RecordAndStop();
        await InState(SessionState.Idle);

        Assert.Equal(0, called);
        Assert.Equal(["えーと明日は３時にあの会議です"], _inj.Texts);
        Assert.DoesNotContain(SessionState.Polishing, _states);
        var n = Assert.Single(_notices);
        Assert.Equal(NoticeKind.Warning, n.Kind);
        Assert.Contains("ベース URL が未設定です", n.Message);
    }

    [Fact]
    public async Task Off_never_polishes()
    {
        _pol.Setup = new PolishSetup.Off();
        await RecordAndStop();
        await InState(SessionState.Idle);
        Assert.Equal(["えーと明日は３時にあの会議です"], _inj.Texts);
        Assert.DoesNotContain(SessionState.Polishing, _states);
        Assert.Empty(_notices);
    }

    [Fact]
    public async Task A_throwing_Prepare_is_treated_as_unconfigured()
    {
        _pol.PrepareError = new InvalidOperationException("settings gone");
        await RecordAndStop();
        await InState(SessionState.Idle);
        Assert.Equal(["えーと明日は３時にあの会議です"], _inj.Texts);
        Assert.Single(_notices);
    }

    [Fact]
    public async Task The_setup_is_fixed_when_recording_starts()
    {
        _pol.Setup = new PolishSetup.Ready((_, _) => Task.FromResult(PolishResult.Success("録音開始時の設定")), TimeSpan.FromSeconds(15));
        _c.Toggle(); await InState(SessionState.Recording);
        _pol.Setup = new PolishSetup.Off(); // 録音中の設定変更は次のセッションから
        _c.Toggle();
        await InState(SessionState.Idle);
        Assert.Equal(["録音開始時の設定"], _inj.Texts);
        Assert.Equal(1, _pol.PrepareCalls);
    }

    [Fact]
    public async Task Inject_failure_after_polishing_keeps_the_polished_text_as_the_single_pending_item()
    {
        _pol.Setup = new PolishSetup.Ready((_, _) => Task.FromResult(PolishResult.Success("整った文")), TimeSpan.FromSeconds(15));
        _inj.Next = new InjectResult(InjectOutcome.Failed, "x");
        await RecordAndStop();
        await InState(SessionState.Failed);
        _inj.Next = new InjectResult(InjectOutcome.Success);
        _c.RetryInsert();
        await InState(SessionState.Idle);
        Assert.Equal(["整った文", "整った文"], _inj.Texts);
    }

    [Fact]
    public async Task Empty_recognition_never_reaches_the_polisher()
    {
        _stt.Result = new RecognitionResult("   ", "cpu");
        var called = false;
        _pol.Setup = new PolishSetup.Ready((_, _) => { called = true; return Task.FromResult(PolishResult.Success("x")); }, TimeSpan.FromSeconds(15));
        await RecordAndStop();
        await InState(SessionState.Idle);
        Assert.False(called);
        Assert.Empty(_inj.Texts);
    }

    private sealed class FakePolisher : ITextPolisher
    {
        public PolishSetup Setup { get; set; } = new PolishSetup.Off();
        public Exception? PrepareError { get; set; }
        public int PrepareCalls { get; private set; }

        public PolishSetup Prepare()
        {
            PrepareCalls++;
            if (PrepareError is not null) throw PrepareError;
            return Setup;
        }
    }
}
