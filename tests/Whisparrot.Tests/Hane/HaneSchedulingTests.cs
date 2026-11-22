using Whisparrot.Core;
using Whisparrot.Core.Hane;

namespace Whisparrot.Tests.Hane;

/// <summary>タイミングを固定したいケース（文脈の予算・ジョブ上限・同じ文書の保留・範囲の失効）を、台本どおりに返すクライアントで検証する。</summary>
internal sealed class ScriptedHaneClient : IHaneClient
{
    private readonly object _gate = new();
    private int _n;
    public List<string> Calls { get; } = [];
    public string DocumentId { get; set; } = "doc";
    public long ExpiresInMs { get; set; } = 120_000;
    public TaskCompletionSource? ContextGate { get; set; }       // 取得を保留する（取消されたら Timeout）
    public Func<Task>? ReplaceGate { get; set; }
    public HaneResult<HaneReplaced>? ReplaceResult { get; set; }
    public List<IReadOnlyList<string>?> PendingSeen { get; } = [];
    public List<string> ReplacedTexts { get; } = [];

    private void Log(string s) { lock (_gate) Calls.Add(s); }

    public async Task<HaneResult<HaneContext>> GetContextAsync(HaneWindow window, HaneContextRequest request, CancellationToken ct)
    {
        Log("context");
        lock (_gate) PendingSeen.Add(request.PendingRangeHandles);
        if (ContextGate is { } g)
        {
            try { await g.Task.WaitAsync(ct); }
            catch (OperationCanceledException) { return HaneResult<HaneContext>.Fail(HaneFailure.Timeout); }
        }
        return HaneResult<HaneContext>.Success(new HaneContext("inst", DocumentId, "1", 0, new HaneSelection(0, 0), "t", false, "absent", null, "", "", false, false, 0, 0, []));
    }

    public Task<HaneResult<HaneInserted>> InsertAsync(HaneWindow window, HaneContext context, string text, CancellationToken ct)
    {
        int n;
        lock (_gate) n = ++_n;
        Log("insert:" + text);
        return Task.FromResult(HaneResult<HaneInserted>.Success(new HaneInserted("inst", context.DocumentId, "2", 0, 1, "h" + n, "hash", Environment.TickCount64 + ExpiresInMs)));
    }

    public async Task<HaneResult<HaneReplaced>> ReplaceAsync(HaneWindow window, HaneInserted inserted, string replacement, CancellationToken ct)
    {
        Log("replace:" + inserted.RangeHandle);
        lock (_gate) ReplacedTexts.Add(replacement);
        if (ReplaceGate is { } g) await g();
        Log("replace-done:" + inserted.RangeHandle);
        return ReplaceResult ?? HaneResult<HaneReplaced>.Success(new HaneReplaced("doc", "3", 0, 1, "merged"));
    }
}

public sealed class HaneSchedulingTests : IDisposable
{
    private readonly FakeRecorder _rec = new();
    private readonly FakeRecognizer _stt = new() { Result = new RecognitionResult("原文", "cpu") };
    private readonly FakeInjector _inj = new() { NextTarget = new FakeWindowTarget(1, 2) };
    private readonly TestPolisher _pol = new();
    private readonly ScriptedHaneClient _hane = new();
    private readonly List<SessionNotice> _notices = [];
    private readonly SessionCoordinator _c;
    private readonly List<TaskCompletionSource<PolishResult>> _gates = [];

    public HaneSchedulingTests()
    {
        // 台本どおりの整文: 呼ばれるたびに新しいゲートを作り、テストが開ける。
        _pol.Setup = new PolishSetup.Ready((raw, _) =>
        {
            var g = new TaskCompletionSource<PolishResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gates) _gates.Add(g);
            return g.Task;
        }, TimeSpan.FromSeconds(30));
        _c = new SessionCoordinator(_rec, _stt, _inj, _pol, new TestHaneLink(() => new HaneSetup.Ready(_hane, InsertRawFirst: true)));
        _c.Noticed += n => { lock (_notices) _notices.Add(n); };
    }

    public void Dispose() => _c.Dispose();

    private Task InState(SessionState s) => Wait.UntilAsync(() => _c.Snapshot.State == s, $"state {s} (now {_c.Snapshot.State})");

    private async Task Dictate()
    {
        _c.Toggle(); await InState(SessionState.Recording);
        await Wait.UntilAsync(() => _hane.Calls.Count(c => c == "context") >= 1, "context", 1000);
        await Task.Delay(40);
        await _c.FlushAsync();
        _c.Toggle();
        await InState(SessionState.Idle);
    }

    private static void Complete(TaskCompletionSource<PolishResult> g, string text) => g.SetResult(PolishResult.Success(text));

    [Fact]
    public async Task A_context_that_misses_the_150_ms_budget_means_this_session_uses_the_legacy_insertion()
    {
        _hane.ContextGate = new TaskCompletionSource(); // 開かない
        _pol.Setup = new PolishSetup.Off();
        _c.Toggle(); await InState(SessionState.Recording);
        await Task.Delay(300); // 予算（150 ms）を超えて待たせた後に停止する
        _c.Toggle(); await InState(SessionState.Idle);

        Assert.Equal(["原文"], _inj.Texts);
        Assert.DoesNotContain(_hane.Calls, c => c.StartsWith("insert"));
        Assert.Empty(_notices);
    }

    [Fact]
    public async Task A_context_that_arrives_after_recognition_is_ignored_and_the_session_stays_legacy()
    {
        _hane.ContextGate = new TaskCompletionSource();
        _pol.Setup = new PolishSetup.Off();
        _c.Toggle(); await InState(SessionState.Recording);
        _c.Toggle(); await InState(SessionState.Idle); // 認識は即座に終わる。文脈はまだ届いていない
        _hane.ContextGate.SetResult();
        await Task.Delay(100);
        Assert.Equal(["原文"], _inj.Texts);
        Assert.DoesNotContain(_hane.Calls, c => c.StartsWith("insert"));
    }

    [Fact]
    public async Task A_third_concurrent_polish_is_skipped_in_favour_of_the_raw_text_with_a_notice()
    {
        await Dictate();
        await Dictate();
        Assert.Equal(2, _c.ActiveJobCount);
        await Dictate(); // 上限（2 件）。原文は入るが整文は省略される
        Assert.Equal(2, _c.ActiveJobCount);
        Assert.Equal(3, _hane.Calls.Count(c => c.StartsWith("insert")));
        Assert.Contains(_notices, n => n.Message.Contains("混み合って"));
        Assert.Equal(2, _gates.Count);
    }

    [Fact]
    public async Task A_replace_is_held_while_a_session_is_recording_into_the_same_document_and_sent_after_its_insert()
    {
        await Dictate();                                    // A: 原文を挿入、整文はゲート待ち
        _c.Toggle(); await InState(SessionState.Recording); // B: 同じ文書へ録音中
        await Wait.UntilAsync(() => _hane.Calls.Count(c => c == "context") == 2, "B context", 1000);
        await Task.Delay(40); await _c.FlushAsync();

        Complete(_gates[0], "A の整文");                     // A の整文が終わっても、B が録音・認識中の間は送出しない
        await Task.Delay(150);
        Assert.DoesNotContain(_hane.Calls, c => c.StartsWith("replace:"));
        Assert.Equal(1, _c.ActiveJobCount);

        _c.Toggle(); await InState(SessionState.Idle);      // B の挿入が確定して Idle へ
        await Wait.UntilAsync(() => _hane.Calls.Any(c => c.StartsWith("replace-done:")), "A replaced");
        var order = _hane.Calls.Where(c => c.StartsWith("insert") || c.StartsWith("replace:")).ToArray();
        Assert.Equal(["insert:原文", "insert:原文", "replace:h1"], order); // B の挿入 → A の差し替え
        Assert.Equal(["A の整文"], _hane.ReplacedTexts);
    }

    [Fact]
    public async Task The_next_context_request_lists_the_ranges_still_waiting_for_their_polish()
    {
        await Dictate();
        await Dictate();
        Assert.Null(_hane.PendingSeen[0]?.Count > 0 ? _hane.PendingSeen[0] : null);
        Assert.Equal(["h1"], _hane.PendingSeen[1]);
        await Dictate();
        Assert.Equal(["h1", "h2"], _hane.PendingSeen[2]); // 最大 2 件
    }

    [Fact]
    public async Task A_held_replace_whose_handle_expires_keeps_the_raw_text_and_offers_the_polished_text()
    {
        _hane.ExpiresInMs = 400;
        await Dictate();
        _c.Toggle(); await InState(SessionState.Recording);
        await Wait.UntilAsync(() => _hane.Calls.Count(c => c == "context") == 2, "B context", 1000);
        await Task.Delay(40); await _c.FlushAsync();

        Complete(_gates[0], "遅れた整文");                   // B が録音中のまま、ハンドルの期限（400 ms）を過ぎる
        await Wait.UntilAsync(() => _c.Held is not null, "expired", 3000);

        Assert.DoesNotContain(_hane.Calls, c => c.StartsWith("replace:"));
        Assert.Equal(new HeldText(HeldKind.PolishedNotApplied, "遅れた整文"), _c.Held);
        Assert.Contains(_notices, n => n.Message == "整文結果は反映されませんでした");
        Assert.Equal(0, _c.ActiveJobCount);
    }

    [Fact]
    public async Task A_replace_already_on_the_wire_is_awaited_by_the_next_context_request()
    {
        var release = new TaskCompletionSource();
        _hane.ReplaceGate = () => release.Task;
        await Dictate();
        Complete(_gates[0], "整文");
        await Wait.UntilAsync(() => _hane.Calls.Contains("replace:h1"), "replace sent");

        _c.Toggle(); await InState(SessionState.Recording);
        await Task.Delay(60);
        Assert.Equal(1, _hane.Calls.Count(c => c == "context")); // 差し替えの確定待ち。次の文脈要求はまだ出ていない
        release.SetResult();
        await Wait.UntilAsync(() => _hane.Calls.Count(c => c == "context") == 2, "context after replace", 1000);
        Assert.True(_hane.Calls.IndexOf("replace-done:h1") < _hane.Calls.LastIndexOf("context"));
    }

    [Fact]
    public async Task A_rejected_replace_is_not_retried()
    {
        _hane.ReplaceResult = HaneResult<HaneReplaced>.Fail(HaneFailure.Remote, "RANGE_CHANGED");
        await Dictate();
        Complete(_gates[0], "整文");
        await Wait.UntilAsync(() => _c.Held is not null, "held");
        await Task.Delay(100);
        Assert.Single(_hane.Calls, c => c.StartsWith("replace:"));
        Assert.Contains(_notices, n => n.Message == "整文結果は反映されませんでした");
    }

    [Fact]
    public async Task The_held_text_is_replaced_by_the_next_result_and_released_on_discard()
    {
        _hane.ReplaceResult = HaneResult<HaneReplaced>.Fail(HaneFailure.Remote, "RANGE_CHANGED");
        await Dictate();
        Complete(_gates[0], "一つ目");
        await Wait.UntilAsync(() => _c.Held?.Text == "一つ目", "first held");
        await Dictate();
        Complete(_gates[1], "二つ目");
        await Wait.UntilAsync(() => _c.Held?.Text == "二つ目", "second held"); // 直近の一件だけを保持
        var seen = new List<HeldText?>();
        _c.HeldChanged += h => seen.Add(h);
        _c.DiscardHeld();
        await Wait.UntilAsync(() => _c.Held is null, "released");
        Assert.Equal([null], seen);
    }

    [Fact]
    public void Held_text_never_appears_in_its_string_form()
    {
        var s = new HeldText(HeldKind.InsertUnconfirmed, "機密の本文").ToString();
        Assert.DoesNotContain("機密", s);
        var ctx = new HaneContext("i", "d", "1", 0, new HaneSelection(0, 0), "タイトル", false, "absent", "fm", "前の本文", "後の本文", false, false, null, null, []);
        Assert.DoesNotContain("本文", ctx.ToString());
        Assert.DoesNotContain("タイトル", ctx.ToString());
    }
}
