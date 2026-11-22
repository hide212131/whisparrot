using Whisparrot.Core;
using Whisparrot.Core.Hane;
using Whisparrot.Infrastructure.Hane;

namespace Whisparrot.Tests.Hane;

internal sealed record FakeWindowTarget(long Hwnd, uint Pid) : IInjectTarget, IWindowTarget;

internal sealed class TestHaneLink(Func<HaneSetup> setup) : IHaneLink
{
    public HaneSetup Prepare() => setup();
}

internal sealed class TestPolisher : ITextPolisher
{
    public PolishSetup Setup { get; set; } = new PolishSetup.Off();
    public PolishSetup Prepare() => Setup;
}

/// <summary>実際の名前付きパイプ上のモック Hane に対する、コーディネーターの先行挿入・条件付き差し替え・フォールバックの検証（W-2 / 設計 §4.5）。</summary>
public sealed class HaneCoordinatorTests : IDisposable
{
    private const string Raw = "えーと原文です";
    private const string Polished = "整文後の文です。";

    private readonly MockHaneServer _srv = new();
    private readonly FakeRecorder _rec = new();
    private readonly FakeRecognizer _stt = new() { Result = new RecognitionResult(Raw, "cpu") };
    private readonly FakeInjector _inj = new();
    private readonly TestPolisher _pol = new();
    private readonly List<SessionState> _states = [];
    private readonly List<SessionNotice> _notices = [];
    private HaneSetup _setup;
    private SessionCoordinator _c = null!;

    public HaneCoordinatorTests()
    {
        Task.Run(HaneClient.WarmUp).GetAwaiter().GetResult(); // 製品でも起動時に行う。初回の JIT が文脈取得の予算（150 ms）を食わないようにする
        _srv.SetDocument("見出し\n前の文。");
        _setup = new HaneSetup.Ready(HaneClientTests.Client(_srv), InsertRawFirst: true);
        _inj.NextTarget = new FakeWindowTarget(_srv.Hwnd, _srv.Pid);
        _pol.Setup = new PolishSetup.Ready((raw, _) => Task.FromResult(PolishResult.Success(Polished)), TimeSpan.FromSeconds(15));
        Build();
    }

    private void Build()
    {
        _c?.Dispose();
        _c = new SessionCoordinator(_rec, _stt, _inj, _pol, new TestHaneLink(() => _setup));
        _c.StateChanged += s => { lock (_states) _states.Add(s.State); };
        _c.Noticed += n => { lock (_notices) _notices.Add(n); };
    }

    public void Dispose()
    {
        _c.Dispose();
        _srv.Dispose();
    }

    private Task InState(SessionState s) => Wait.UntilAsync(() => _c.Snapshot.State == s, $"state {s} (now {_c.Snapshot.State})");

    private async Task RecordAndStop(bool expectContext = true)
    {
        _c.Toggle(); await InState(SessionState.Recording);
        // 録音中に文脈が取れるまで待つ（録音開始を待たせず非同期に取得される）。Hane に届かない構えでは待たない。
        if (expectContext) await Wait.UntilAsync(() => _srv.Of("context.get").Any(), $"context requested (frames: {string.Join(",", Methods)}; state {_c.Snapshot.State}; notices {string.Join("|", _notices.Select(n => n.Message))})", 1000);
        await Task.Delay(80); // 応答がコーディネーターへ戻るまで
        await _c.FlushAsync();
        _c.Toggle();
    }

    private string[] Methods => _srv.Frames.Select(f => f.Method).ToArray();

    private static string DocWith(string tail) => "見出し\n前の文。" + tail;

    [Fact]
    public async Task Raw_text_is_inserted_at_once_and_replaced_later_by_the_polished_text()
    {
        var gate = new TaskCompletionSource<PolishResult>();
        _pol.Setup = new PolishSetup.Ready((_, _) => gate.Task, TimeSpan.FromSeconds(15));
        await RecordAndStop();
        await InState(SessionState.Idle);

        // 整文を待たずに原文が入り、録音スロットは解放済み（次の録音を始められる）。
        Assert.Equal(DocWith(Raw), _srv.Text);
        Assert.Equal(1, _c.ActiveJobCount);
        Assert.Empty(_inj.Texts);

        gate.SetResult(PolishResult.Success(Polished));
        await Wait.UntilAsync(() => _srv.Text == DocWith(Polished), "replaced");
        await Wait.UntilAsync(() => _c.ActiveJobCount == 0, "job done");

        Assert.Equal([SessionState.Recording, SessionState.Recognizing, SessionState.InsertingRaw, SessionState.Idle], _states);
        Assert.Equal(["hello", "context.get", "hello", "edit.insert", "hello", "edit.replace"], Methods);
        Assert.Empty(_notices);
        Assert.Empty(_inj.Texts); // 従来の貼り付けは一度も使わない
        Assert.Null(_c.Held);
    }

    [Fact]
    public async Task Recording_start_does_not_wait_for_the_context()
    {
        _c.Toggle();
        await InState(SessionState.Recording); // 文脈取得の完了を待たずに Recording になっている
        Assert.True(_rec.Active);
    }

    [Fact]
    public async Task A_range_edited_while_polishing_is_not_replaced_and_nothing_is_inserted_twice()
    {
        var gate = new TaskCompletionSource<PolishResult>();
        _pol.Setup = new PolishSetup.Ready((_, _) => gate.Task, TimeSpan.FromSeconds(15));
        await RecordAndStop();
        await InState(SessionState.Idle);

        var start = System.Text.Encoding.UTF8.GetByteCount(DocWith(""));
        _srv.UserEdit(start + 3, 0, "★"); // 整文待ちの間に、挿入範囲の内側を編集する
        var edited = _srv.Text;
        gate.SetResult(PolishResult.Success(Polished));
        await Wait.UntilAsync(() => _c.ActiveJobCount == 0, "job finished");

        Assert.Equal(edited, _srv.Text);                       // 差し替えは拒否され、本文は利用者の編集のまま
        Assert.DoesNotContain(Polished, _srv.Text);           // 整文結果が重ねて入ることもない
        Assert.Single(_srv.Of("edit.replace"));               // 自動再試行しない
        Assert.Empty(_inj.Texts);                              // 追加の貼り付けもしない
        Assert.Contains(_notices, n => n.Message == "整文結果は反映されませんでした");
        Assert.Equal(new HeldText(HeldKind.PolishedNotApplied, Polished), _c.Held);
    }

    [Fact]
    public async Task A_lost_insert_response_is_re_asked_with_the_same_operation_id_and_the_session_continues()
    {
        _srv.DropResponseAfterApply = true;
        await RecordAndStop();
        await InState(SessionState.Idle);
        await Wait.UntilAsync(() => _srv.Text == DocWith(Polished), "replaced");

        var inserts = _srv.Of("edit.insert").ToArray();
        Assert.Equal(2, inserts.Length);
        Assert.Equal(inserts[0].OperationId, inserts[1].OperationId);
        Assert.Equal(1, _srv.Text.Split(Raw).Length - 1 + (_srv.Text.Contains(Polished) ? 1 : 0)); // 原文（または整文後）が一度だけ
        Assert.Empty(_inj.Texts);
        Assert.Empty(_notices);
    }

    [Fact]
    public async Task An_insert_that_stays_unknown_is_never_pasted_automatically_and_is_offered_for_copy()
    {
        _stt.Manual = true;
        _c.Toggle(); await InState(SessionState.Recording);
        _c.Toggle(); await InState(SessionState.Recognizing);
        await Wait.UntilAsync(() => _srv.Of("context.get").Any(), "context");
        _srv.DropEveryConnectionOnMutation = true; // 挿入は応答がなく、再照会も応答がない
        _stt.Complete();
        await InState(SessionState.Idle);

        Assert.Empty(_inj.Texts);                                    // 現行の自動投入へは渡さない
        Assert.Equal(new HeldText(HeldKind.InsertUnconfirmed, Raw), _c.Held);
        Assert.Contains(_notices, n => n.Message == "入力結果を確認できません。Hane の本文を確認してください");
        Assert.DoesNotContain(SessionState.Failed, _states);         // 再試行ボタンにも渡さない
        Assert.False(_c.Snapshot.HasPendingText);

        _c.DiscardHeld();
        await Wait.UntilAsync(() => _c.Held is null, "held released");
    }

    [Fact]
    public async Task With_the_integration_off_the_legacy_path_is_used_and_hane_is_not_contacted()
    {
        _setup = new HaneSetup.Off();
        await RecordAndStop(expectContext: false);
        await InState(SessionState.Idle);

        Assert.Equal([Polished], _inj.Texts); // 従来経路（整文してから一度だけ投入）
        Assert.Same(_inj.NextTarget, _inj.TargetsSeen.Single()); // 録音開始時の投入先で貼る（従来どおり）
        Assert.Empty(_srv.Frames);
    }

    [Fact]
    public async Task Without_a_link_at_all_nothing_changes()
    {
        _c.Dispose();
        _c = new SessionCoordinator(_rec, _stt, _inj, _pol);
        _c.Noticed += n => { lock (_notices) _notices.Add(n); };
        await RecordAndStop(expectContext: false);
        await Wait.UntilAsync(() => _inj.Texts.Count == 1 || _c.Snapshot.State == SessionState.Polishing, "legacy");
        Assert.Empty(_srv.Frames);
    }

    [Fact]
    public async Task When_hane_is_not_running_the_session_falls_back_to_the_legacy_insertion()
    {
        File.Delete(Path.Combine(_srv.DescriptorDir, $"{_srv.Pid}.json"));
        _pol.Setup = new PolishSetup.Off();
        await RecordAndStop(expectContext: false);
        await InState(SessionState.Idle);

        Assert.Equal([Raw], _inj.Texts);
        Assert.Same(_inj.NextTarget, _inj.TargetsSeen.Single());
        Assert.Empty(_srv.Frames);
        Assert.Empty(_notices); // 連携が成立しないだけで、通知は出さない
    }

    [Fact]
    public async Task A_non_markdown_target_falls_back_to_the_legacy_insertion()
    {
        _srv.ContextError = "UNSUPPORTED_DOCUMENT";
        _pol.Setup = new PolishSetup.Off();
        await RecordAndStop();
        await InState(SessionState.Idle);
        Assert.Equal([Raw], _inj.Texts);
        Assert.Empty(_srv.Of("edit.insert"));
    }

    [Fact]
    public async Task A_target_that_is_not_a_window_target_never_contacts_hane()
    {
        _inj.NextTarget = new FakeTarget("not a window");
        _pol.Setup = new PolishSetup.Off();
        await RecordAndStop(expectContext: false);
        await InState(SessionState.Idle);
        Assert.Equal([Raw], _inj.Texts);
        Assert.Empty(_srv.Frames);
    }

    [Fact]
    public async Task A_cursor_moved_during_the_session_is_rejected_without_auto_paste_and_the_text_is_kept_for_an_explicit_retry()
    {
        _stt.Manual = true;
        _c.Toggle(); await InState(SessionState.Recording);
        _c.Toggle(); await InState(SessionState.Recognizing);
        await Wait.UntilAsync(() => _srv.Of("context.get").Any(), "context");
        _srv.MoveCaret(0); // 録音中に利用者がカーソルを動かした
        _stt.Complete();
        await InState(SessionState.Failed);

        Assert.Empty(_inj.Texts);
        Assert.True(_c.Snapshot.HasPendingText);
        Assert.Equal(DocWith(""), _srv.Text);
        Assert.Contains(_notices, n => n.Kind == NoticeKind.Error && n.Message.Contains("カーソル"));

        _c.RetryInsert(); // 利用者の明示操作だけが従来入力（その時点の前景）へ切り替える
        await InState(SessionState.Idle);
        Assert.Equal([Raw], _inj.Texts);
        Assert.Null(_inj.TargetsSeen.Single());
    }

    [Fact]
    public async Task With_raw_first_off_the_polished_text_is_inserted_once_after_polishing()
    {
        _setup = new HaneSetup.Ready(HaneClientTests.Client(_srv), InsertRawFirst: false);
        var gate = new TaskCompletionSource<PolishResult>();
        _pol.Setup = new PolishSetup.Ready((_, _) => gate.Task, TimeSpan.FromSeconds(15));
        await RecordAndStop();
        await InState(SessionState.Polishing);
        Assert.Equal(DocWith(""), _srv.Text); // 整文の間は何も入らない

        gate.SetResult(PolishResult.Success(Polished));
        await InState(SessionState.Idle);
        Assert.Equal(DocWith(Polished), _srv.Text);
        Assert.Single(_srv.Of("edit.insert"));
        Assert.Empty(_srv.Of("edit.replace"));
        Assert.Empty(_inj.Texts);
        Assert.Equal(0, _c.ActiveJobCount);
    }

    [Fact]
    public async Task With_raw_first_off_a_failed_polish_inserts_the_raw_text_through_hane()
    {
        _setup = new HaneSetup.Ready(HaneClientTests.Client(_srv), InsertRawFirst: false);
        _pol.Setup = new PolishSetup.Ready((_, _) => Task.FromResult(PolishResult.Fallback(PolishFailure.Network)), TimeSpan.FromSeconds(15));
        await RecordAndStop();
        await InState(SessionState.Idle);
        Assert.Equal(DocWith(Raw), _srv.Text);
        Assert.Empty(_inj.Texts);
    }

    [Fact]
    public async Task A_failed_polish_after_the_raw_insert_keeps_the_raw_text_and_says_so()
    {
        _pol.Setup = new PolishSetup.Ready((_, _) => Task.FromResult(PolishResult.Fallback(PolishFailure.Timeout)), TimeSpan.FromSeconds(15));
        await RecordAndStop();
        await InState(SessionState.Idle);
        await Wait.UntilAsync(() => _c.ActiveJobCount == 0, "job done");
        Assert.Equal(DocWith(Raw), _srv.Text);
        Assert.Empty(_srv.Of("edit.replace"));
        Assert.Contains(_notices, n => n.Message == "整文できなかったため原文を残しました");
    }

    [Fact]
    public async Task A_polish_result_identical_to_the_raw_text_edits_nothing()
    {
        _pol.Setup = new PolishSetup.Ready((raw, _) => Task.FromResult(PolishResult.Success(raw)), TimeSpan.FromSeconds(15));
        await RecordAndStop();
        await InState(SessionState.Idle);
        await Wait.UntilAsync(() => _c.ActiveJobCount == 0, "job done");
        Assert.Empty(_srv.Of("edit.replace"));
        Assert.Empty(_notices);
    }

    [Fact]
    public async Task With_polishing_off_the_raw_text_is_the_final_text()
    {
        _pol.Setup = new PolishSetup.Off();
        await RecordAndStop();
        await InState(SessionState.Idle);
        Assert.Equal(DocWith(Raw), _srv.Text);
        Assert.Equal(0, _c.ActiveJobCount);
    }

    [Fact]
    public async Task A_lost_replace_response_is_re_asked_with_the_same_operation_id_and_succeeds_quietly()
    {
        var gate = new TaskCompletionSource<PolishResult>();
        _pol.Setup = new PolishSetup.Ready((_, _) => gate.Task, TimeSpan.FromSeconds(15));
        await RecordAndStop();
        await InState(SessionState.Idle);
        _srv.DropResponseAfterApply = true;
        gate.SetResult(PolishResult.Success(Polished));
        await Wait.UntilAsync(() => _c.ActiveJobCount == 0, "job done");

        var replaces = _srv.Of("edit.replace").ToArray();
        Assert.Equal(2, replaces.Length);
        Assert.Equal(replaces[0].OperationId, replaces[1].OperationId);
        Assert.Equal(DocWith(Polished), _srv.Text);
        Assert.Empty(_notices);
        Assert.Null(_c.Held);
    }

    [Fact]
    public async Task A_replace_that_stays_unknown_is_reported_and_never_resent_or_pasted()
    {
        var gate = new TaskCompletionSource<PolishResult>();
        _pol.Setup = new PolishSetup.Ready((_, _) => gate.Task, TimeSpan.FromSeconds(15));
        await RecordAndStop();
        await InState(SessionState.Idle);
        _srv.DropEveryConnectionOnMutation = true;
        gate.SetResult(PolishResult.Success(Polished));
        await Wait.UntilAsync(() => _c.ActiveJobCount == 0, "job done");

        Assert.Equal(2, _srv.Of("edit.replace").Count());
        Assert.Contains(_notices, n => n.Message == "整文の反映を確認できません");
        Assert.Equal(HeldKind.PolishedNotApplied, _c.Held!.Kind);
        Assert.Empty(_inj.Texts);
        Assert.Equal(DocWith(Raw), _srv.Text);
    }

    [Fact]
    public async Task Cancel_after_the_raw_insert_cancels_only_the_polish()
    {
        var gate = new TaskCompletionSource<PolishResult>();
        _pol.Setup = new PolishSetup.Ready((_, _) => gate.Task, TimeSpan.FromSeconds(15));
        await RecordAndStop();
        await InState(SessionState.Idle);
        Assert.Equal(1, _c.ActiveJobCount);

        _c.Cancel();
        await Wait.UntilAsync(() => _c.ActiveJobCount == 0, "job cancelled");
        gate.SetResult(PolishResult.Success(Polished));
        await Task.Delay(150);
        Assert.Equal(DocWith(Raw), _srv.Text); // 原文はそのまま（Undo は利用者に委ねる）
        Assert.Empty(_srv.Of("edit.replace"));
    }

    [Fact]
    public async Task A_second_dictation_into_the_same_document_passes_the_pending_range_and_is_not_disturbed_by_the_first_replace()
    {
        var gate = new TaskCompletionSource<PolishResult>();
        var calls = 0;
        _pol.Setup = new PolishSetup.Ready((raw, _) => calls++ == 0 ? gate.Task : Task.FromResult(PolishResult.Success("二つ目の整文。")), TimeSpan.FromSeconds(15));
        _stt.Result = new RecognitionResult("一つ目", "cpu");
        await RecordAndStop();
        await InState(SessionState.Idle);
        Assert.Equal(DocWith("一つ目"), _srv.Text);

        // 遅い整文の間に、次の録音を始めて原文を入れる。
        _stt.Result = new RecognitionResult("二つ目", "cpu");
        await RecordAndStop();
        await InState(SessionState.Idle);
        await Wait.UntilAsync(() => _srv.Text == DocWith("一つ目二つ目の整文。"), "second replaced");

        var second = _srv.Of("context.get").Last();
        Assert.Single(second.Params["pendingRangeHandles"]!.AsArray()); // 整文確定待ちの範囲を知らせる

        gate.SetResult(PolishResult.Success("一つ目の整文。"));
        await Wait.UntilAsync(() => _srv.Text == DocWith("一つ目の整文。二つ目の整文。"), "first replaced");
        Assert.Empty(_notices);
    }
}
