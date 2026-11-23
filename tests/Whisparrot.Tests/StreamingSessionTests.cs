using Whisparrot.Core;
using Whisparrot.Core.Settings;

namespace Whisparrot.Tests;

/// <summary>
/// S-2: ストリーミングのセッション。区間は全サンプルが連番の値の PCM で流し、認識のモックが値から区間を識別する。
/// 並行度・期限・順序・取消・失敗継続・停止時のフラッシュ・飽和・OFF 時の不変性を、ループスレッド越しに確かめる。
/// </summary>
public sealed class StreamingSessionTests : IDisposable
{
    private const int Interval = 4;

    private sealed class Harness : IDisposable
    {
        public readonly FakeStreamingRecorder Rec = new();
        public readonly FakeInjector Inj = new() { NextTarget = new FakeTarget("t") };
        public readonly SessionCoordinator C;
        public readonly List<SessionNotice> Notices = [];
        public readonly List<SessionState> States = [];
        public readonly List<bool> StreamingFlags = [];

        public Harness(Func<RecognitionRequest, CancellationToken, Task<RecognitionResult>> recognize,
            Func<string, CancellationToken, Task<PolishResult>>? polish = null, TimeSpan? polishTimeout = null, StreamingLimits? limits = null,
            StreamingSettings? settings = null, RecognitionEngine engine = RecognitionEngine.Cloud, IRecorder? recorder = null)
        {
            var s = settings ?? new StreamingSettings(true, Interval);
            C = new SessionCoordinator(recorder ?? Rec, new FakeRecognitionSource(recognize, engine), Inj,
                polish is null ? null : new FakePolisher(polish, polishTimeout ?? TimeSpan.FromSeconds(15)), null, () => s);
            if (limits is not null) C.StreamingLimits = limits;
            C.StateChanged += st => { lock (States) { States.Add(st.State); StreamingFlags.Add(st.IsStreaming); } };
            C.Noticed += n => { lock (Notices) Notices.Add(n); };
        }

        public Task InState(SessionState st) => Wait.UntilAsync(() => C.Snapshot.State == st, $"state {st} (now {C.Snapshot.State})");
        public Task Injected(int n) => Wait.UntilAsync(() => Inj.Segments.Count >= n, $"{n} injections (now {Inj.Segments.Count})");
        public string[] Messages { get { lock (Notices) return Notices.Select(n => n.Message).ToArray(); } }
        public void Dispose() => C.Dispose();
    }

    private readonly List<Harness> _all = [];
    public void Dispose() { foreach (var h in _all) h.Dispose(); }

    private Harness Make(Func<RecognitionRequest, CancellationToken, Task<RecognitionResult>>? recognize = null, Func<string, CancellationToken, Task<PolishResult>>? polish = null,
        TimeSpan? polishTimeout = null, StreamingLimits? limits = null, StreamingSettings? settings = null, RecognitionEngine engine = RecognitionEngine.Cloud)
    {
        var h = new Harness(recognize ?? ((r, _) => Task.FromResult(new RecognitionResult($"文{Id(r)}", "cloud"))), polish, polishTimeout, limits, settings, engine);
        _all.Add(h);
        return h;
    }

    private static int Id(RecognitionRequest r) => (int)r.Pcm.Samples[0];

    private static StreamingLimits Fast(Func<StreamingLimits, StreamingLimits>? tweak = null)
    {
        var l = new StreamingLimits { Tick = TimeSpan.FromMilliseconds(10) };
        return tweak?.Invoke(l) ?? l;
    }

    private static async Task<Harness> Start(Harness h)
    {
        h.C.Toggle();
        await h.InState(SessionState.Recording);
        return h;
    }

    /// <summary>手動で完了させる認識。区間 k の完了を <see cref="Complete"/> で指示する。</summary>
    private sealed class ManualRecognizer
    {
        private readonly object _l = new();
        private readonly Dictionary<int, TaskCompletionSource<RecognitionResult>> _t = [];
        public readonly List<int> Started = [];
        public int StartedCount { get { lock (_l) return Started.Count; } }

        public Task<RecognitionResult> Run(RecognitionRequest r, CancellationToken ct)
        {
            var id = Id(r);
            lock (_l)
            {
                Started.Add(id);
                var tcs = new TaskCompletionSource<RecognitionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                _t[id] = tcs;
                return tcs.Task;
            }
        }

        public void Complete(int id, string? text = null) { lock (_l) _t[id].TrySetResult(new RecognitionResult(text ?? $"文{id}", "cloud")); }
        public void Fail(int id) { lock (_l) _t[id].TrySetException(new RecognitionException("x")); }
    }

    // ── 順序・並行 ──

    [Fact]
    public async Task Segments_are_injected_in_order_during_recording_and_the_tail_flows_on_stop()
    {
        var h = Make();
        await Start(h);
        h.Rec.PushSeconds(1, 4);
        h.Rec.PushSeconds(2, 4);
        await h.Injected(2);
        Assert.Equal(SessionState.Recording, h.C.Snapshot.State); // 録音中に複数回入っている
        Assert.True(h.C.Snapshot.IsStreaming);

        h.Rec.PushSeconds(3, 1); // 末尾
        h.C.Toggle();
        await h.InState(SessionState.Idle);

        Assert.Equal(["文1", "文2", "文3"], h.Inj.Segments);
        Assert.Contains("入力 3 区間・原文のまま 0 区間・欠落 0 区間", h.Messages);
        Assert.Equal(1, h.Rec.StopCount);
        Assert.Equal(0, h.Rec.StartCount); // 一括の Start は使わない
        Assert.Contains(SessionState.Flushing, h.States);
        Assert.Equal(SessionState.Idle, h.States[^1]);
        Assert.False(h.C.Snapshot.IsStreaming);
    }

    [Fact]
    public async Task Out_of_order_recognition_completion_never_overtakes()
    {
        var m = new ManualRecognizer();
        var h = Make(m.Run);
        await Start(h);
        h.Rec.PushSeconds(1, 4);
        h.Rec.PushSeconds(2, 4);
        await Wait.UntilAsync(() => m.StartedCount == 2, "two in flight");
        h.Rec.PushSeconds(3, 4);

        m.Complete(2);
        await Task.Delay(100);
        Assert.Empty(h.Inj.Segments); // 1 がまだ。2 は待つ
        m.Complete(1);
        await h.Injected(2);
        await Wait.UntilAsync(() => m.StartedCount == 3, "third starts after a slot frees");
        m.Complete(3);
        await h.Injected(3);

        Assert.Equal(["文1", "文2", "文3"], h.Inj.Segments);
    }

    [Fact]
    public async Task Cloud_recognition_runs_two_at_a_time_and_starts_in_sequence_order()
    {
        var m = new ManualRecognizer();
        var h = Make(m.Run);
        await Start(h);
        for (var k = 1; k <= 4; k++) h.Rec.PushSeconds(k, 4);

        await Wait.UntilAsync(() => m.StartedCount == 2, "two start");
        await Task.Delay(150);
        Assert.Equal([1, 2], m.Started.OrderBy(x => x)); // 連番の若い順に枠を埋める（開始の前後はスレッドの都合）
        m.Complete(1);
        await Wait.UntilAsync(() => m.StartedCount == 3, "third");
        Assert.Equal([1, 2, 3], m.Started.OrderBy(x => x));
    }

    [Fact]
    public async Task Polish_completion_reversal_keeps_output_order_and_uses_the_polished_text()
    {
        var gates = new Dictionary<string, TaskCompletionSource<PolishResult>>();
        Task<PolishResult> Polish(string raw, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<PolishResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gates) gates[raw] = tcs;
            return tcs.Task;
        }
        var h = Make(polish: Polish);
        await Start(h);
        h.Rec.PushSeconds(1, 4);
        h.Rec.PushSeconds(2, 4);
        await Wait.UntilAsync(() => { lock (gates) return gates.Count == 2; }, "both polishing");

        lock (gates) gates["文2"].SetResult(PolishResult.Success("整2"));
        await Task.Delay(100);
        Assert.Empty(h.Inj.Segments); // 汎用は整文の完了を待って順番に投入。先頭が済むまで出さない
        lock (gates) gates["文1"].SetResult(PolishResult.Success("整1"));
        await h.Injected(2);

        Assert.Equal(["整1", "整2"], h.Inj.Segments);
    }

    [Fact]
    public async Task Polish_concurrency_is_capped_at_two()
    {
        var running = 0;
        var peak = 0;
        var release = new TaskCompletionSource();
        async Task<PolishResult> Polish(string raw, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref running);
            int p;
            while (now > (p = Volatile.Read(ref peak))) Interlocked.CompareExchange(ref peak, now, p);
            await release.Task;
            Interlocked.Decrement(ref running);
            return PolishResult.Success("整" + raw);
        }
        var h = Make(polish: Polish);
        await Start(h);
        for (var k = 1; k <= 4; k++) h.Rec.PushSeconds(k, 4);
        await Wait.UntilAsync(() => Volatile.Read(ref running) == 2, "two polishing");
        await Task.Delay(100);
        Assert.Equal(2, Volatile.Read(ref running));
        release.SetResult();
        await h.Injected(4);

        Assert.Equal(2, Volatile.Read(ref peak));
        Assert.Equal(["整文1", "整文2", "整文3", "整文4"], h.Inj.Segments);
    }

    [Fact]
    public async Task Injection_is_serialized_one_at_a_time()
    {
        var h = Make();
        var first = new TaskCompletionSource<InjectResult>();
        h.Inj.SegmentHandler = (t, _) => t == "文1" ? first.Task : Task.FromResult(new InjectResult(InjectOutcome.Success));
        await Start(h);
        h.Rec.PushSeconds(1, 4);
        h.Rec.PushSeconds(2, 4);

        await h.Injected(1);
        await Task.Delay(150);
        Assert.Single(h.Inj.Segments); // s1 の投入が終わるまで s2 を送らない
        first.SetResult(new InjectResult(InjectOutcome.Success));
        await h.Injected(2);
        Assert.Equal(["文1", "文2"], h.Inj.Segments);
    }

    [Fact]
    public async Task Alphanumeric_boundaries_get_one_space_and_japanese_boundaries_none()
    {
        var texts = new Dictionary<int, string> { [1] = "hello", [2] = "world", [3] = "です", [4] = "ok", [5] = "あ" };
        var h = Make((r, _) => Task.FromResult(new RecognitionResult(texts[Id(r)], "cloud")));
        await Start(h);
        for (var k = 1; k <= 5; k++) h.Rec.PushSeconds(k, 4);
        await h.Injected(5);

        Assert.Equal(["hello", " world", "です", "ok", "あ"], h.Inj.Segments); // 英数字どうしの境界（hello|world）だけ空白を足す。日本語が絡む境界は足さない
    }

    // ── 期限 ──

    [Fact]
    public async Task Recognition_past_its_deadline_is_dropped_and_a_late_success_is_ignored()
    {
        var m = new ManualRecognizer();
        var h = Make(m.Run, limits: Fast(l => l with { RecognitionDeadline = TimeSpan.FromMilliseconds(150) }));
        await Start(h);
        h.Rec.PushSeconds(1, 4);
        h.Rec.PushSeconds(2, 4);
        await Wait.UntilAsync(() => m.StartedCount == 2, "both start");
        m.Complete(1);
        await h.Injected(1);
        // 2 は 150 ms 後に期限切れ → 欠落。後続 3 は期限内に済み、2 を待たずに入る。
        h.Rec.PushSeconds(3, 4);
        await Wait.UntilAsync(() => m.StartedCount == 3, "third starts once a slot frees");
        m.Complete(3);
        await h.Injected(2);
        m.Complete(2); // 期限後の遅い成功
        await Task.Delay(100);

        Assert.Equal(["文1", "文3"], h.Inj.Segments);
        h.C.Toggle();
        await h.InState(SessionState.Idle);
        Assert.Equal(["文1", "文3"], h.Inj.Segments); // 遅い完了で二重・追い越しの投入は起きない
        Assert.Contains(h.Messages, m2 => m2.Contains("入力 2 区間") && m2.Contains("欠落 1 区間"));
    }

    [Fact]
    public async Task The_execution_slot_is_not_reused_until_the_call_actually_ends()
    {
        var m = new ManualRecognizer();
        var h = Make(m.Run, limits: Fast(l => l with { RecognitionDeadline = TimeSpan.FromMilliseconds(100) }));
        await Start(h);
        h.Rec.PushSeconds(1, 4);
        h.Rec.PushSeconds(2, 4);
        await Wait.UntilAsync(() => m.StartedCount == 2, "both start");
        await Task.Delay(400); // 1・2 は期限切れで欠落扱いだが、呼び出しは終わっていない（取消を無視する認識）
        h.Rec.PushSeconds(3, 4);
        await Task.Delay(50);

        Assert.Equal(2, m.StartedCount); // 枠は空かないので 3 は始まらない
        m.Complete(1);                   // 呼び出しが終わって初めて枠が空く
        await Wait.UntilAsync(() => m.StartedCount == 3, "third starts after a real end");
    }

    [Fact]
    public async Task Polish_that_times_out_falls_back_to_the_raw_text_and_is_counted()
    {
        var h = Make(polish: (_, ct) => Task.Delay(Timeout.Infinite, ct).ContinueWith<PolishResult>(_ => PolishResult.Fallback(PolishFailure.Timeout)),
            polishTimeout: TimeSpan.FromMilliseconds(120), limits: Fast());
        await Start(h);
        h.Rec.PushSeconds(1, 4);
        await h.Injected(1);
        h.C.Toggle();
        await h.InState(SessionState.Idle);

        Assert.Equal(["文1"], h.Inj.Segments);
        Assert.Contains("入力 1 区間・原文のまま 1 区間・欠落 0 区間", h.Messages);
    }

    [Fact]
    public async Task Polish_failure_and_empty_result_fall_back_to_raw()
    {
        var h = Make(polish: (raw, _) => Task.FromResult(raw == "文1" ? PolishResult.Fallback(PolishFailure.Http, "HTTP 500") : PolishResult.Success("  ")));
        await Start(h);
        h.Rec.PushSeconds(1, 4);
        h.Rec.PushSeconds(2, 4);
        await h.Injected(2);

        Assert.Equal(["文1", "文2"], h.Inj.Segments);
    }

    [Fact]
    public async Task Ready_segment_waiting_for_the_output_slot_past_the_wait_limit_is_dropped()
    {
        var h = Make(limits: Fast(l => l with { OutputWait = TimeSpan.FromMilliseconds(200) }));
        var first = new TaskCompletionSource<InjectResult>();
        h.Inj.SegmentHandler = (t, _) => t == "文1" ? first.Task : Task.FromResult(new InjectResult(InjectOutcome.Success));
        await Start(h);
        h.Rec.PushSeconds(1, 4);
        h.Rec.PushSeconds(2, 4);
        await h.Injected(1);
        await Task.Delay(500); // s2 は出力枠待ちのまま 200 ms を過ぎる
        first.SetResult(new InjectResult(InjectOutcome.Success));
        h.C.Toggle();
        await h.InState(SessionState.Idle);

        Assert.Equal(["文1"], h.Inj.Segments); // 打ち切られた s2 は後から入らない
        Assert.Contains("入力 1 区間・原文のまま 0 区間・欠落 1 区間", h.Messages);
    }

    // ── 停止・フラッシュ ──

    [Fact]
    public async Task Stop_flushes_remaining_segments_in_order_even_if_they_complete_in_reverse()
    {
        var m = new ManualRecognizer();
        var h = Make(m.Run);
        await Start(h);
        h.Rec.PushSeconds(1, 4);
        h.Rec.PushSeconds(2, 1);
        h.C.Toggle();
        await h.InState(SessionState.Flushing);
        await Wait.UntilAsync(() => m.StartedCount == 2, "both start");
        m.Complete(2);
        await Task.Delay(100);
        Assert.Empty(h.Inj.Segments);
        m.Complete(1);
        await h.InState(SessionState.Idle);

        Assert.Equal(["文1", "文2"], h.Inj.Segments);
    }

    [Fact]
    public async Task At_the_flush_deadline_unrecognized_segments_are_dropped_and_ready_ones_still_go_out()
    {
        var m = new ManualRecognizer();
        var h = Make(m.Run, limits: Fast(l => l with { Flush = TimeSpan.FromMilliseconds(200), SendWindow = TimeSpan.FromMilliseconds(500) }));
        await Start(h);
        h.Rec.PushSeconds(1, 4);
        h.Rec.PushSeconds(2, 4);
        h.Rec.PushSeconds(3, 1);
        await Wait.UntilAsync(() => m.StartedCount == 2, "two start");
        m.Complete(1);
        await h.Injected(1);
        h.C.Toggle();
        await h.InState(SessionState.Idle); // 2・3 は認識が終わらず、フラッシュ期限で欠落になって終わる

        Assert.Equal(["文1"], h.Inj.Segments);
        Assert.Contains(h.Messages, x => x.Contains("入力 1 区間") && x.Contains("欠落 2 区間"));
    }

    [Fact]
    public async Task At_the_flush_deadline_polishing_segments_go_out_as_raw()
    {
        var h = Make(polish: (_, ct) => Task.Delay(Timeout.Infinite, ct).ContinueWith<PolishResult>(_ => PolishResult.Fallback(PolishFailure.Timeout)),
            limits: Fast(l => l with { Flush = TimeSpan.FromMilliseconds(200) }));
        await Start(h);
        h.Rec.PushSeconds(1, 4);
        h.C.Toggle();
        await h.InState(SessionState.Idle);

        Assert.Equal(["文1"], h.Inj.Segments);
        Assert.Contains("入力 1 区間・原文のまま 1 区間・欠落 0 区間", h.Messages);
    }

    [Fact]
    public async Task Ready_segments_not_sent_within_the_send_window_are_dropped()
    {
        var h = Make(limits: Fast(l => l with { Flush = TimeSpan.FromMilliseconds(100), SendWindow = TimeSpan.FromMilliseconds(150) }));
        var first = new TaskCompletionSource<InjectResult>();
        h.Inj.SegmentHandler = (t, _) => t == "文1" ? first.Task : Task.FromResult(new InjectResult(InjectOutcome.Success));
        await Start(h);
        h.Rec.PushSeconds(1, 4);
        h.Rec.PushSeconds(2, 4);
        await h.Injected(1);
        h.C.Toggle();
        await Task.Delay(700); // 送出枠（100+150 ms）を過ぎる。s1 の投入は進行中
        first.SetResult(new InjectResult(InjectOutcome.Success));
        await h.InState(SessionState.Idle);

        Assert.Equal(["文1"], h.Inj.Segments);
        Assert.Contains("入力 1 区間・原文のまま 0 区間・欠落 1 区間", h.Messages);
    }

    [Fact]
    public async Task No_audio_at_all_says_so_and_returns_to_idle()
    {
        var h = Make();
        await Start(h);
        h.C.Toggle();
        await h.InState(SessionState.Idle);

        Assert.Empty(h.Inj.Segments);
        Assert.Contains("音声がありませんでした", h.Messages);
    }

    [Fact]
    public async Task A_new_recording_is_refused_until_the_flush_finishes()
    {
        var m = new ManualRecognizer();
        var h = Make(m.Run);
        await Start(h);
        h.Rec.PushSeconds(1, 1);
        h.C.Toggle();
        await h.InState(SessionState.Flushing);
        h.C.Toggle(); // 無視される
        await h.C.FlushAsync();
        Assert.Equal(1, h.Rec.StreamStarts);
        m.Complete(1);
        await h.InState(SessionState.Idle);
        h.C.Toggle();
        await Wait.UntilAsync(() => h.Rec.StreamStarts == 2, "second recording");
    }

    // ── 取消 ──

    [Fact]
    public async Task Cancel_discards_pending_segments_and_nothing_is_injected_afterwards()
    {
        var m = new ManualRecognizer();
        var h = Make(m.Run);
        await Start(h);
        h.Rec.PushSeconds(1, 4);
        h.Rec.PushSeconds(2, 4);
        await Wait.UntilAsync(() => m.StartedCount == 2, "both in flight");

        h.C.Cancel();
        await h.InState(SessionState.Idle);
        m.Complete(1); // 取消後の遅い完了
        m.Complete(2);
        await Task.Delay(150);

        Assert.Empty(h.Inj.Segments);
        Assert.Equal(1, h.Rec.CancelCount);
        Assert.Contains("取消しました。入力済みの内容は残ります", h.Messages);
        Assert.False(h.C.Snapshot.IsStreaming);
    }

    [Fact]
    public async Task Cancel_while_a_segment_is_being_injected_stops_later_ones_and_warns_about_the_sent_one()
    {
        var h = Make();
        var first = new TaskCompletionSource<InjectResult>();
        h.Inj.SegmentHandler = (t, _) => t == "文1" ? first.Task : Task.FromResult(new InjectResult(InjectOutcome.Success));
        await Start(h);
        h.Rec.PushSeconds(1, 4);
        h.Rec.PushSeconds(2, 4);
        await h.Injected(1);

        h.C.Cancel();
        await h.InState(SessionState.Idle);
        Assert.True(h.Inj.SegmentTokens[0].IsCancellationRequested); // 副作用前なら投入側が中止できる
        first.SetResult(new InjectResult(InjectOutcome.Success));
        await Task.Delay(150);

        Assert.Equal(["文1"], h.Inj.Segments); // 送出前の s2 は入らない
        Assert.Contains(h.Messages, x => x.Contains("送信済みの入力は反映される場合があります"));
    }

    [Fact]
    public async Task Cancel_during_flush_works_too()
    {
        var m = new ManualRecognizer();
        var h = Make(m.Run);
        await Start(h);
        h.Rec.PushSeconds(1, 2);
        h.C.Toggle();
        await h.InState(SessionState.Flushing);
        h.C.Cancel();
        await h.InState(SessionState.Idle);
        m.Complete(1);
        await Task.Delay(100);

        Assert.Empty(h.Inj.Segments);
    }

    // ── 失敗しても継続 ──

    [Fact]
    public async Task A_failed_segment_is_dropped_and_the_rest_continue()
    {
        var h = Make((r, _) => Id(r) == 2
            ? Task.FromException<RecognitionResult>(new RecognitionException("boom"))
            : Task.FromResult(new RecognitionResult($"文{Id(r)}", "cloud")));
        await Start(h);
        h.Rec.PushSeconds(1, 4);
        h.Rec.PushSeconds(2, 4);
        h.Rec.PushSeconds(3, 4);
        await h.Injected(2);
        h.C.Toggle();
        await h.InState(SessionState.Idle);

        Assert.Equal(["文1", "文3"], h.Inj.Segments);
        Assert.Contains("入力 2 区間・原文のまま 0 区間・欠落 1 区間", h.Messages);
    }

    [Fact]
    public async Task Empty_recognition_is_counted_apart_from_drops_and_injects_nothing()
    {
        var h = Make((r, _) => Task.FromResult(new RecognitionResult(Id(r) == 2 ? "  " : $"文{Id(r)}", "cloud")));
        await Start(h);
        h.Rec.PushSeconds(1, 4);
        h.Rec.PushSeconds(2, 4);
        h.Rec.PushSeconds(3, 4);
        await h.Injected(2);
        h.C.Toggle();
        await h.InState(SessionState.Idle);

        Assert.Equal(["文1", "文3"], h.Inj.Segments);
        Assert.Contains("入力 2 区間・原文のまま 0 区間・欠落 0 区間・空 1 区間", h.Messages);
    }

    [Fact]
    public async Task Three_consecutive_drops_warn_once_per_session()
    {
        var h = Make((_, _) => Task.FromException<RecognitionResult>(new RecognitionException("x")));
        await Start(h);
        for (var k = 1; k <= 5; k++) h.Rec.PushSeconds(k, 4);
        await Wait.UntilAsync(() => h.Messages.Any(x => x.Contains("認識できない区間が続いています")), "warning");
        h.C.Toggle();
        await h.InState(SessionState.Idle);

        Assert.Single(h.Messages, x => x.Contains("認識できない区間が続いています"));
        Assert.Contains("入力 0 区間・原文のまま 0 区間・欠落 5 区間", h.Messages);
    }

    [Fact]
    public async Task Injection_failure_stops_further_auto_injection_and_holds_only_the_last_one()
    {
        var h = Make();
        h.Inj.SegmentHandler = (t, _) => Task.FromResult(t == "文2" ? new InjectResult(InjectOutcome.Failed, "入力先が変わったため、入力しませんでした") : new InjectResult(InjectOutcome.Success));
        await Start(h);
        h.Rec.PushSeconds(1, 4);
        h.Rec.PushSeconds(2, 4);
        h.Rec.PushSeconds(3, 4);
        await Wait.UntilAsync(() => h.C.Held is not null, "held");
        await h.InState(SessionState.Idle);

        Assert.Equal(HeldKind.StreamingNotInjected, h.C.Held!.Kind);
        Assert.Equal("文2", h.C.Held.Text);
        Assert.DoesNotContain("文3", h.Inj.Segments); // 以降は自動投入しない
        Assert.Equal(1, h.Rec.CancelCount);          // 録音も止める
        Assert.Contains(h.Notices, n => n.Kind == NoticeKind.Error);
    }

    [Fact]
    public async Task Unknown_effect_is_reported_apart_from_a_plain_failure()
    {
        var h = Make();
        h.Inj.SegmentHandler = (_, _) => Task.FromResult(new InjectResult(InjectOutcome.Failed, "x", EffectUnknown: true));
        await Start(h);
        h.Rec.PushSeconds(1, 4);
        await h.InState(SessionState.Idle);

        Assert.Contains(h.Messages, x => x.Contains("確認できない"));
        Assert.Contains(h.Messages, x => x.Contains("適用未確認 1 区間"));
        Assert.Equal("文1", h.C.Held!.Text);
    }

    // ── 飽和・上限・録音側の事象 ──

    [Fact]
    public async Task Saturation_drops_new_segments_beyond_the_buffer_limit_but_keeps_sequence_order()
    {
        var m = new ManualRecognizer();
        var h = Make(m.Run, limits: Fast(l => l with { MaxBufferedSegments = 3 }));
        await Start(h);
        for (var k = 1; k <= 6; k++) h.Rec.PushSeconds(k, 4); // 4〜6 は上限超えで欠落
        await Wait.UntilAsync(() => m.StartedCount == 2, "two in flight");
        m.Complete(1);
        m.Complete(2);
        await Wait.UntilAsync(() => m.StartedCount == 3, "third");
        m.Complete(3);
        await h.Injected(3);
        h.C.Toggle();
        await h.InState(SessionState.Idle);

        Assert.Equal(["文1", "文2", "文3"], h.Inj.Segments);
        Assert.Equal([1, 2, 3], m.Started.OrderBy(x => x));
        Assert.Contains("入力 3 区間・原文のまま 0 区間・欠落 3 区間", h.Messages);
    }

    [Fact]
    public async Task The_recording_limit_stops_the_microphone_and_flushes()
    {
        var h = Make(limits: Fast(l => l with { MaxRecording = TimeSpan.FromSeconds(5) }));
        await Start(h);
        h.Rec.PushSeconds(1, 4);
        h.Rec.PushSeconds(2, 2); // 6 秒 > 5 秒
        await h.InState(SessionState.Idle);

        Assert.Equal(1, h.Rec.StopCount);
        Assert.Equal(["文1", "文2"], h.Inj.Segments);
        Assert.Contains(h.Notices, n => n.Kind == NoticeKind.Warning && n.Message.Contains("までです"));
    }

    [Fact]
    public async Task Microphone_disconnect_flushes_what_was_captured()
    {
        var h = Make();
        await Start(h);
        h.Rec.PushSeconds(1, 4);
        h.Rec.PushSeconds(2, 1);
        h.Rec.RaiseFailed(new RecorderError(RecorderErrorKind.Disconnected, "マイクが切断されました"));
        h.Rec.Cancel(); // 実デバイスはチャンネルを完了して解放する
        await h.InState(SessionState.Idle);

        Assert.Equal(["文1", "文2"], h.Inj.Segments);
        Assert.Contains("マイクが切断されました", h.Messages);
    }

    [Fact]
    public async Task A_gap_in_the_audio_splits_segments_without_merging_across_it()
    {
        var h = Make();
        await Start(h);
        h.Rec.PushSeconds(1, 1);
        h.Rec.Skip(16000);
        h.Rec.PushSeconds(2, 1);
        h.C.Toggle();
        await h.InState(SessionState.Idle);

        Assert.Equal(["文1", "文2"], h.Inj.Segments);
    }

    [Fact]
    public async Task Recording_start_failure_is_reported_and_leaves_idle()
    {
        var h = Make();
        h.Rec.StartError = new RecorderException(RecorderErrorKind.DeviceUnavailable, "マイクを使えません");
        h.C.Toggle();
        await h.C.FlushAsync();

        Assert.Equal(SessionState.Idle, h.C.Snapshot.State);
        Assert.Contains("マイクを使えません", h.Messages);
    }

    // ── 一括への分岐・OFF 時の不変性 ──

    [Fact]
    public async Task When_streaming_is_off_the_batch_path_runs_exactly_as_before()
    {
        var h = Make(settings: new StreamingSettings(false, 8));
        h.C.Toggle();
        await h.InState(SessionState.Recording);
        h.C.Toggle();
        await h.InState(SessionState.Idle);

        Assert.Equal(1, h.Rec.StartCount);
        Assert.Equal(0, h.Rec.StreamStarts);
        Assert.Equal([SessionState.Recording, SessionState.Recognizing, SessionState.Inserting, SessionState.Idle], h.States);
        Assert.All(h.StreamingFlags, f => Assert.False(f));
        Assert.Empty(h.Inj.Segments); // InjectSegmentAsync は使わない
        Assert.Single(h.Inj.Texts);
    }

    [Fact]
    public async Task Without_a_streaming_setting_provider_nothing_changes()
    {
        var rec = new FakeStreamingRecorder();
        var inj = new FakeInjector();
        using var c = new SessionCoordinator(rec, new FakeRecognitionSource((r, _) => Task.FromResult(new RecognitionResult("x", "cloud"))), inj);
        c.Toggle();
        await Wait.UntilAsync(() => c.Snapshot.State == SessionState.Recording, "recording");
        c.Toggle();
        await Wait.UntilAsync(() => c.Snapshot.State == SessionState.Idle, "idle");

        Assert.Equal(1, rec.StartCount);
        Assert.Equal(0, rec.StreamStarts);
        Assert.Equal(["x"], inj.Texts);
    }

    [Fact]
    public async Task A_recorder_without_continuous_capture_uses_batch_even_when_enabled()
    {
        var plain = new FakeRecorder();
        var h = new Harness((r, _) => Task.FromResult(new RecognitionResult("バッチ", "cloud")), recorder: plain);
        _all.Add(h);
        h.C.Toggle();
        await h.InState(SessionState.Recording);
        h.C.Toggle();
        await h.InState(SessionState.Idle);

        Assert.Equal(["バッチ"], h.Inj.Texts);
        Assert.Empty(h.Inj.Segments);
    }

    [Fact]
    public async Task Local_recognition_falls_back_to_batch_with_a_notice()
    {
        var h = Make(engine: RecognitionEngine.Local);
        h.C.Toggle();
        await h.InState(SessionState.Recording);
        h.C.Toggle();
        await h.InState(SessionState.Idle);

        Assert.Equal(1, h.Rec.StartCount);
        Assert.Equal(0, h.Rec.StreamStarts);
        Assert.Contains(h.Messages, x => x.Contains("クラウド認識のときだけ"));
        Assert.Empty(h.Inj.Segments);
    }

    [Fact]
    public async Task Streaming_without_a_recordable_target_does_not_start_recording()
    {
        var h = Make();
        h.Inj.NextTarget = null;
        h.C.Toggle();
        await h.C.FlushAsync();

        Assert.Equal(SessionState.Idle, h.C.Snapshot.State);
        Assert.Equal(0, h.Rec.StreamStarts);
        Assert.Equal(0, h.Rec.StartCount);
        Assert.Contains(h.Notices, n => n.Kind == NoticeKind.Error);
    }

    [Fact]
    public void Streaming_limits_defaults_are_the_design_values()
    {
        var l = new StreamingLimits();
        Assert.Equal(TimeSpan.FromSeconds(24), l.RecognitionDeadline);
        Assert.Equal(TimeSpan.FromSeconds(32), l.PolishCutoff);
        Assert.Equal(TimeSpan.FromSeconds(40), l.OutputWait);
        Assert.Equal(TimeSpan.FromSeconds(25), l.Flush);
        Assert.Equal(TimeSpan.FromSeconds(5), l.SendWindow);
        Assert.Equal(TimeSpan.FromMinutes(20), l.MaxRecording);
        Assert.Equal(2, l.CloudRecognitionConcurrency);
        Assert.Equal(2, l.PolishConcurrency);
        Assert.Equal(8, l.MaxBufferedSegments);
    }
}
