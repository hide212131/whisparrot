using Whisparrot.Core;
using Whisparrot.Core.Hane;
using Whisparrot.Infrastructure.Hane;

namespace Whisparrot.Tests.Hane;

/// <summary>コーディネーターの流れ（挿入 → 文脈付き整文 → 差し替え）と、文脈 OFF・整文失敗時の従来の挙動（W-3 / 設計 §4.5・§4.6）。実際のパイプ上のモック Hane を使う。</summary>
public sealed class HaneContextPolishTests : IDisposable
{
    private const string Raw = "えーとハネの設計です";
    private const string Polished = "Hane の設計です。";

    private readonly MockHaneServer _srv = new();
    private readonly FakeRecorder _rec = new();
    private readonly FakeRecognizer _stt = new() { Result = new RecognitionResult(Raw, "cpu") };
    private readonly FakeInjector _inj = new();
    private readonly TestPolisher _pol = new();
    private readonly List<SessionNotice> _notices = [];
    private readonly List<PolishInput> _contextCalls = [];
    private readonly List<string> _legacyCalls = [];
    private Func<PolishInput, Task<PolishResult>> _onContext;
    private Func<bool> _allowed = () => true;
    private bool _insertRawFirst = true;
    private SessionCoordinator _c = null!;

    public HaneContextPolishTests()
    {
        Task.Run(HaneClient.WarmUp).GetAwaiter().GetResult();
        _srv.SetDocument("# 設計メモ\nクラウドストレージの前の文。");
        _inj.NextTarget = new FakeWindowTarget(_srv.Hwnd, _srv.Pid);
        _onContext = _ => Task.FromResult(PolishResult.Success(Polished));
        _pol.Setup = new PolishSetup.Ready(
            (raw, _) => { lock (_legacyCalls) _legacyCalls.Add(raw); return Task.FromResult(PolishResult.Success(Polished)); },
            TimeSpan.FromSeconds(15))
        {
            PolishWithContextAsync = (input, _) => { lock (_contextCalls) _contextCalls.Add(input); return _onContext(input); },
        };
        Build();
    }

    private void Build()
    {
        _c?.Dispose();
        var link = new TestHaneLink(() => new HaneSetup.Ready(HaneClientTests.Client(_srv), _insertRawFirst) { ContextAllowed = () => _allowed() });
        _c = new SessionCoordinator(_rec, _stt, _inj, _pol, link);
        _c.Noticed += n => { lock (_notices) _notices.Add(n); };
    }

    public void Dispose()
    {
        _c.Dispose();
        _srv.Dispose();
    }

    private Task InState(SessionState s) => Wait.UntilAsync(() => _c.Snapshot.State == s, $"state {s} (now {_c.Snapshot.State})");

    private async Task RecordAndStop()
    {
        _c.Toggle(); await InState(SessionState.Recording);
        await Wait.UntilAsync(() => _srv.Of("context.get").Any(), "context requested", 1000);
        await Task.Delay(80);
        await _c.FlushAsync();
        _c.Toggle();
    }

    [Fact]
    public async Task Raw_is_inserted_first_then_polished_with_the_context_and_replaced()
    {
        await RecordAndStop();
        await InState(SessionState.Idle);
        await Wait.UntilAsync(() => _srv.Text.EndsWith(Polished), "replaced");
        await Wait.UntilAsync(() => _c.ActiveJobCount == 0, "job done");

        var call = Assert.Single(_contextCalls);
        Assert.Equal(Raw, call.Utterance);
        Assert.Contains("クラウドストレージの前の文。", call.Context!.Before);
        Assert.Equal("mock.md", call.Context.Title);
        Assert.Contains("クラウドストレージ", call.Context.Terms);
        Assert.Empty(_legacyCalls);
        Assert.Equal(["hello", "context.get", "hello", "edit.insert", "hello", "edit.replace"], _srv.Frames.Select(f => f.Method).ToArray());
        Assert.Empty(_notices);
        Assert.Empty(_inj.Texts);
    }

    [Fact]
    public async Task With_context_sending_off_the_polish_is_the_legacy_one_and_no_context_is_sent()
    {
        _allowed = () => false;
        await RecordAndStop();
        await InState(SessionState.Idle);
        await Wait.UntilAsync(() => _srv.Text.EndsWith(Polished), "replaced");

        Assert.Empty(_contextCalls);
        Assert.Equal([Raw], _legacyCalls);
        // Hane への挿入・差し替えそのものは従来どおり。
        Assert.Single(_srv.Of("edit.insert"));
        Assert.Single(_srv.Of("edit.replace"));
    }

    [Fact]
    public void The_default_setup_does_not_allow_context_sending()
    {
        Assert.False(new HaneSetup.Ready(HaneClientTests.Client(_srv), true).ContextAllowed());
        Assert.False(new Whisparrot.Core.Settings.HaneSettings(true, true).SendContextToPolish);
    }

    [Fact]
    public async Task A_permission_withdrawn_before_the_polish_is_started_is_honored()
    {
        _stt.Manual = true;
        _c.Toggle(); await InState(SessionState.Recording);
        await Wait.UntilAsync(() => _srv.Of("context.get").Any(), "context", 1000);
        await Task.Delay(80);
        _c.Toggle(); await InState(SessionState.Recognizing);
        _allowed = () => false; // 認識の途中で利用者が許可を外した
        _stt.Complete();
        await InState(SessionState.Idle);
        await Wait.UntilAsync(() => _c.ActiveJobCount == 0, "job done");

        Assert.Empty(_contextCalls);
        Assert.Equal([Raw], _legacyCalls);
    }

    [Fact]
    public async Task A_permission_withdrawn_just_before_sending_removes_the_context_from_the_call()
    {
        var checks = 0;
        _allowed = () => Interlocked.Increment(ref checks) == 1; // 組み立て時は許可、送信直前の確認では不許可
        await RecordAndStop();
        await InState(SessionState.Idle);
        await Wait.UntilAsync(() => _c.ActiveJobCount == 0, "job done");

        Assert.Empty(_contextCalls);
        Assert.Equal([Raw], _legacyCalls);
        Assert.True(checks >= 2);
    }

    [Fact]
    public async Task A_polish_failure_keeps_the_raw_text_and_says_so()
    {
        _onContext = _ => Task.FromResult(PolishResult.Fallback(PolishFailure.Http, "HTTP 500"));
        await RecordAndStop();
        await InState(SessionState.Idle);
        await Wait.UntilAsync(() => _c.ActiveJobCount == 0, "job done");

        Assert.EndsWith(Raw, _srv.Text);
        Assert.Empty(_srv.Of("edit.replace"));
        Assert.Contains(_notices, n => n.Message == "整文できなかったため原文を残しました");
        Assert.Null(_c.Held);
    }

    [Fact]
    public async Task A_polish_timeout_keeps_the_raw_text()
    {
        _pol.Setup = new PolishSetup.Ready((raw, _) => Task.FromResult(PolishResult.Success(Polished)), TimeSpan.FromMilliseconds(150))
        {
            PolishWithContextAsync = (_, ct) => new TaskCompletionSource<PolishResult>().Task, // 返ってこない
        };
        await RecordAndStop();
        await InState(SessionState.Idle);
        await Wait.UntilAsync(() => _c.ActiveJobCount == 0, "job done");

        Assert.EndsWith(Raw, _srv.Text);
        Assert.Empty(_srv.Of("edit.replace"));
        Assert.Contains(_notices, n => n.Message == "整文できなかったため原文を残しました");
    }

    [Fact]
    public async Task A_replace_rejected_for_a_changed_range_is_not_forced_even_with_a_context_polish()
    {
        var gate = new TaskCompletionSource<PolishResult>();
        _onContext = _ => gate.Task;
        await RecordAndStop();
        await InState(SessionState.Idle);

        var start = System.Text.Encoding.UTF8.GetByteCount(_srv.Text) - System.Text.Encoding.UTF8.GetByteCount(Raw);
        _srv.UserEdit(start + 3, 0, "★");
        var edited = _srv.Text;
        gate.SetResult(PolishResult.Success(Polished));
        await Wait.UntilAsync(() => _c.ActiveJobCount == 0, "job finished");

        Assert.Equal(edited, _srv.Text);
        Assert.Single(_srv.Of("edit.replace"));
        Assert.Contains(_notices, n => n.Message == "整文結果は反映されませんでした");
        Assert.Equal(new HeldText(HeldKind.PolishedNotApplied, Polished), _c.Held);
    }

    [Fact]
    public async Task With_raw_first_off_the_context_polish_runs_before_the_single_insert()
    {
        _insertRawFirst = false;
        Build();
        await RecordAndStop();
        await InState(SessionState.Idle);

        var call = Assert.Single(_contextCalls);
        Assert.Contains("前の文。", call.Context!.Before);
        Assert.EndsWith(Polished, _srv.Text);
        Assert.Single(_srv.Of("edit.insert"));
        Assert.Empty(_srv.Of("edit.replace"));
    }

    [Fact]
    public async Task When_the_context_could_not_be_fetched_the_polish_is_the_legacy_one()
    {
        _srv.ContextError = "UNSUPPORTED_DOCUMENT";
        await RecordAndStop();
        await InState(SessionState.Idle);
        await Wait.UntilAsync(() => _inj.Texts.Count == 1, "legacy injection");

        Assert.Empty(_contextCalls);
        Assert.Equal([Raw], _legacyCalls);
        Assert.Equal([Polished], _inj.Texts);
    }

    [Fact]
    public async Task An_implementation_without_a_context_entry_is_polished_with_the_utterance_only()
    {
        _pol.Setup = new PolishSetup.Ready((raw, _) => { lock (_legacyCalls) _legacyCalls.Add(raw); return Task.FromResult(PolishResult.Success(Polished)); }, TimeSpan.FromSeconds(15));
        await RecordAndStop();
        await InState(SessionState.Idle);
        await Wait.UntilAsync(() => _srv.Text.EndsWith(Polished), "replaced");
        Assert.Equal([Raw], _legacyCalls);
    }

    [Fact]
    public async Task The_previous_utterance_still_waiting_to_be_polished_is_excluded_from_the_next_context()
    {
        var gate = new TaskCompletionSource<PolishResult>();
        _onContext = input => gate.Task;
        await RecordAndStop();
        await InState(SessionState.Idle);
        Assert.EndsWith(Raw, _srv.Text); // 一つ目の原文が文書にある（整文待ち）

        _contextCalls.Clear();
        _stt.Result = new RecognitionResult("二つ目の発話", "cpu");
        _c.Toggle(); await InState(SessionState.Recording);
        await Wait.UntilAsync(() => _srv.Of("context.get").Count() == 2, "second context", 1000);
        await Task.Delay(80);
        await _c.FlushAsync();
        _c.Toggle();
        await InState(SessionState.Idle);
        await Wait.UntilAsync(() => _contextCalls.Count == 1, "second polish started");

        var before = _contextCalls[0].Context!.Before;
        Assert.DoesNotContain(Raw, before);                       // 整文待ちの原文は文脈に入れない
        Assert.Contains("クラウドストレージの前の文。", before);   // その手前の本文は残る
        gate.SetResult(PolishResult.Success(Polished));
    }
}
