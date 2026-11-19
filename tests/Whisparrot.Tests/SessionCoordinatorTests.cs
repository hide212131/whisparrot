using Whisparrot.Core;

namespace Whisparrot.Tests;

public sealed class SessionCoordinatorTests : IDisposable
{
    private readonly FakeRecorder _rec = new();
    private readonly FakeRecognizer _stt = new();
    private readonly FakeInjector _inj = new();
    private readonly SessionCoordinator _c;
    private readonly List<SessionState> _states = [];
    private readonly List<SessionNotice> _notices = [];
    private readonly List<string> _backends = [];

    public SessionCoordinatorTests()
    {
        _c = new SessionCoordinator(_rec, _stt, _inj);
        _c.StateChanged += s => { lock (_states) _states.Add(s.State); };
        _c.Noticed += n => { lock (_notices) _notices.Add(n); };
        _c.BackendReported += b => { lock (_backends) _backends.Add(b); };
    }

    public void Dispose() => _c.Dispose();

    private Task InState(SessionState s) => Wait.UntilAsync(() => _c.Snapshot.State == s, $"state {s} (now {_c.Snapshot.State})");

    [Fact]
    public async Task Normal_path_goes_through_every_state_and_injects_once()
    {
        _c.Toggle();
        await InState(SessionState.Recording);
        Assert.True(_rec.Active);

        _c.Toggle();
        await InState(SessionState.Idle);

        Assert.Equal([SessionState.Recording, SessionState.Recognizing, SessionState.Inserting, SessionState.Idle], _states);
        Assert.Equal(["こんにちは"], _inj.Texts);
        Assert.Equal(["cpu"], _backends);
        Assert.False(_rec.Active);
    }

    [Fact]
    public async Task Stop_button_behaves_like_the_second_toggle()
    {
        _c.Toggle();
        await InState(SessionState.Recording);
        _c.Stop();
        await InState(SessionState.Idle);
        Assert.Single(_inj.Texts);
    }

    [Fact]
    public async Task Cancel_while_recording_releases_the_device_and_never_recognizes()
    {
        _c.Toggle();
        await InState(SessionState.Recording);
        _c.Cancel();
        await InState(SessionState.Idle);

        Assert.False(_rec.Active);
        Assert.Equal(1, _rec.CancelCount);
        Assert.Equal(0, _stt.Calls);
        Assert.Empty(_inj.Texts);
    }

    [Fact]
    public async Task Cancel_while_recognizing_cancels_the_token_and_drops_a_late_completion()
    {
        _stt.Manual = true;
        _c.Toggle();
        await InState(SessionState.Recording);
        _c.Toggle();
        await InState(SessionState.Recognizing);
        await Wait.UntilAsync(() => _stt.Calls == 1, "recognize called");

        _c.Cancel();
        await InState(SessionState.Idle);
        Assert.True(_stt.LastToken.IsCancellationRequested);

        // 取消後に遅れて成功が届いても、投入しない・状態を動かさない
        _stt.CompleteLate(new RecognitionResult("遅れて届いた", "cpu"));
        await Task.Delay(100);
        await _c.FlushAsync();
        Assert.Empty(_inj.Texts);
        Assert.Equal(SessionState.Idle, _c.Snapshot.State);
    }

    [Fact]
    public async Task A_new_session_after_cancel_is_not_confused_by_the_old_late_completion()
    {
        _stt.Manual = true;
        _c.Toggle(); await InState(SessionState.Recording);
        _c.Toggle(); await InState(SessionState.Recognizing);
        _c.Cancel(); await InState(SessionState.Idle);

        _c.Toggle(); await InState(SessionState.Recording);
        _c.Toggle(); await InState(SessionState.Recognizing);
        await Wait.UntilAsync(() => _stt.Calls == 2, "second recognize");
        _stt.Complete();
        await InState(SessionState.Idle);
        Assert.Single(_inj.Texts);
    }

    [Fact]
    public async Task Recognition_error_returns_to_idle_with_an_error_notice_and_no_injection()
    {
        _stt.Error = new RecognitionException("認識子が異常終了しました");
        _c.Toggle(); await InState(SessionState.Recording);
        _c.Toggle(); await InState(SessionState.Idle);

        Assert.Empty(_inj.Texts);
        Assert.Contains(_notices, n => n.Kind == NoticeKind.Error && n.Message.Contains("異常終了"));
    }

    [Fact]
    public async Task Empty_recognition_injects_nothing()
    {
        _stt.Result = new RecognitionResult("  ", "cpu");
        _c.Toggle(); await InState(SessionState.Recording);
        _c.Toggle(); await InState(SessionState.Idle);
        Assert.Empty(_inj.Texts);
        Assert.Contains(_notices, n => n.Kind == NoticeKind.Info);
    }

    [Fact]
    public async Task Empty_audio_skips_recognition()
    {
        _rec.Pcm = new PcmAudio([]);
        _c.Toggle(); await InState(SessionState.Recording);
        _c.Toggle(); await InState(SessionState.Idle);
        Assert.Equal(0, _stt.Calls);
    }

    [Fact]
    public async Task Microphone_start_failure_stays_idle_and_releases_the_device()
    {
        _rec.StartError = new RecorderException(RecorderErrorKind.DeviceUnavailable, "マイクを使えません");
        _c.Toggle();
        await _c.FlushAsync();
        Assert.Equal(SessionState.Idle, _c.Snapshot.State);
        Assert.Equal(1, _rec.CancelCount);
        Assert.Contains(_notices, n => n.Message == "マイクを使えません");
    }

    [Fact]
    public async Task Microphone_failure_during_recording_returns_to_idle_and_releases_the_device()
    {
        _c.Toggle(); await InState(SessionState.Recording);
        _rec.RaiseFailed(new RecorderError(RecorderErrorKind.Disconnected, "マイクが切断されました"));
        await InState(SessionState.Idle);
        Assert.False(_rec.Active);
        Assert.Equal(1, _rec.CancelCount);
        Assert.Equal(0, _stt.Calls);
        Assert.Contains(_notices, n => n.Message == "マイクが切断されました");
    }

    [Fact]
    public async Task Cancel_while_the_recorder_is_still_stopping_drops_the_stop_result()
    {
        _rec.HoldStop = true;
        _c.Toggle(); await InState(SessionState.Recording);
        _c.Toggle();                       // 停止処理に入る（末尾回収中）
        await Task.Delay(50);
        _c.Cancel();
        await InState(SessionState.Idle);
        _rec.CompleteStop();               // 遅れて停止が完了
        await Task.Delay(100);
        await _c.FlushAsync();
        Assert.Equal(0, _stt.Calls);
        Assert.Equal(SessionState.Idle, _c.Snapshot.State);
    }

    [Fact]
    public async Task Model_not_ready_does_not_start_recording()
    {
        _stt.NotReady = "モデルがありません";
        _c.Toggle();
        await _c.FlushAsync();
        Assert.Equal(SessionState.Idle, _c.Snapshot.State);
        Assert.Equal(0, _rec.StartCount);
        Assert.Contains(_notices, n => n.Message == "モデルがありません");
    }

    [Fact]
    public async Task Hotkey_during_recognition_does_not_start_a_new_recording()
    {
        _stt.Manual = true;
        _c.Toggle(); await InState(SessionState.Recording);
        _c.Toggle(); await InState(SessionState.Recognizing);
        _c.Toggle(); _c.Toggle();
        await _c.FlushAsync();
        Assert.Equal(1, _rec.StartCount);
        Assert.Equal(SessionState.Recognizing, _c.Snapshot.State);
    }

    [Fact]
    public async Task Cancel_after_the_inject_commit_point_is_ignored()
    {
        _inj.Manual = true;
        _c.Toggle(); await InState(SessionState.Recording);
        _c.Toggle(); await InState(SessionState.Inserting);
        _c.Cancel();
        await _c.FlushAsync();
        Assert.Equal(SessionState.Inserting, _c.Snapshot.State);
        _inj.Release(new InjectResult(InjectOutcome.Success));
        await InState(SessionState.Idle);
        Assert.Single(_inj.Texts);
    }

    [Fact]
    public async Task Inject_failure_keeps_one_pending_text_and_retry_injects_it_once_more()
    {
        _inj.Next = new InjectResult(InjectOutcome.Failed, "入力先のウィンドウが見つかりません");
        _c.Toggle(); await InState(SessionState.Recording);
        _c.Toggle(); await InState(SessionState.Failed);
        Assert.True(_c.Snapshot.HasPendingText);

        _inj.Next = new InjectResult(InjectOutcome.Success);
        _c.RetryInsert();
        await InState(SessionState.Idle);
        Assert.Equal(["こんにちは", "こんにちは"], _inj.Texts);
        Assert.False(_c.Snapshot.HasPendingText);
    }

    // ---- M10: 投入先は録音開始時に固定する ----

    [Fact]
    public async Task The_target_is_captured_when_recording_starts_and_used_for_the_injection_even_if_focus_moves_later()
    {
        var a = new FakeTarget("A");
        _inj.NextTarget = a;
        _c.Toggle(); await InState(SessionState.Recording);
        Assert.Equal(1, _inj.Captures);
        _inj.NextTarget = new FakeTarget("B"); // 録音中に別アプリへ移った（その時点の前景を取り直してはいけない）
        _c.Toggle(); await InState(SessionState.Idle);
        Assert.Equal(1, _inj.Captures);
        Assert.Same(a, Assert.Single(_inj.TargetsSeen));
    }

    [Fact]
    public async Task No_capturable_target_falls_back_to_the_foreground_at_injection_time()
    {
        _inj.NextTarget = null; // トレイから開始した場合など
        _c.Toggle(); await InState(SessionState.Recording);
        _c.Toggle(); await InState(SessionState.Idle);
        Assert.Null(Assert.Single(_inj.TargetsSeen));
    }

    [Fact]
    public async Task A_capture_that_throws_does_not_stop_recording_and_falls_back()
    {
        _inj.CaptureThrows = true;
        _c.Toggle(); await InState(SessionState.Recording);
        _c.Toggle(); await InState(SessionState.Idle);
        Assert.Null(Assert.Single(_inj.TargetsSeen));
        Assert.Single(_inj.Texts);
    }

    [Fact]
    public async Task A_failed_recorded_injection_keeps_the_text_and_the_retry_targets_the_foreground_at_that_time()
    {
        var a = new FakeTarget("A");
        _inj.NextTarget = a;
        _inj.Next = new InjectResult(InjectOutcome.Failed, "入力先のウィンドウが見つかりません");
        _c.Toggle(); await InState(SessionState.Recording);
        _c.Toggle(); await InState(SessionState.Failed);
        Assert.True(_c.Snapshot.HasPendingText);

        _inj.Next = new InjectResult(InjectOutcome.Success);
        _c.RetryInsert();
        await InState(SessionState.Idle);
        Assert.Equal(2, _inj.TargetsSeen.Count);
        Assert.Same(a, _inj.TargetsSeen[0]);
        Assert.Null(_inj.TargetsSeen[1]);
        Assert.Equal(["こんにちは", "こんにちは"], _inj.Texts);
    }

    [Fact]
    public async Task A_cancelled_recording_leaves_no_target_for_the_next_one()
    {
        _inj.NextTarget = new FakeTarget("A");
        _c.Toggle(); await InState(SessionState.Recording);
        _c.Cancel(); await InState(SessionState.Idle);
        _inj.NextTarget = new FakeTarget("B");
        _c.Toggle(); await InState(SessionState.Recording);
        _c.Toggle(); await InState(SessionState.Idle);
        Assert.Equal("B", Assert.IsType<FakeTarget>(Assert.Single(_inj.TargetsSeen)).Name);
        Assert.Equal(2, _inj.Captures);
    }

    [Fact]
    public async Task A_recorder_that_fails_to_start_does_not_leak_its_target_into_the_next_session()
    {
        _inj.NextTarget = new FakeTarget("A");
        _rec.StartError = new InvalidOperationException("mic");
        _c.Toggle(); await _c.FlushAsync();
        Assert.Equal(SessionState.Idle, _c.Snapshot.State);
        _rec.StartError = null;
        _inj.NextTarget = null;
        _c.Toggle(); await InState(SessionState.Recording);
        _c.Toggle(); await InState(SessionState.Idle);
        Assert.Null(Assert.Single(_inj.TargetsSeen));
    }

    [Fact]
    public async Task Cancel_in_failed_state_discards_the_pending_text()
    {
        _inj.Next = new InjectResult(InjectOutcome.Failed, "x");
        _c.Toggle(); await InState(SessionState.Recording);
        _c.Toggle(); await InState(SessionState.Failed);
        _c.Cancel();
        await InState(SessionState.Idle);
        Assert.False(_c.Snapshot.HasPendingText);
        _c.RetryInsert();
        await _c.FlushAsync();
        Assert.Single(_inj.Texts);
    }

    [Fact]
    public async Task Injector_exception_is_treated_as_a_failure_not_a_crash()
    {
        var c2Inj = new ThrowingInjector();
        using var c2 = new SessionCoordinator(new FakeRecorder(), new FakeRecognizer(), c2Inj);
        c2.Toggle();
        await Wait.UntilAsync(() => c2.Snapshot.State == SessionState.Recording, "rec");
        c2.Toggle();
        await Wait.UntilAsync(() => c2.Snapshot.State == SessionState.Failed, "failed");
        Assert.True(c2.Snapshot.HasPendingText);
    }

    private sealed class ThrowingInjector : ITextInjector
    {
        public Task<InjectResult> InjectAsync(string text, CancellationToken ct) => throw new InvalidOperationException("boom");
    }
}
