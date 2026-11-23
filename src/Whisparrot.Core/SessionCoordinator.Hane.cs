using Whisparrot.Core.Hane;

namespace Whisparrot.Core;

/// <summary>保持している一件（設計 §4.5）。本文はメモリ限りで、ログ・診断に出さない。</summary>
public enum HeldKind
{
    /// <summary>Hane への挿入結果を確認できなかった原文。</summary>
    InsertUnconfirmed,

    /// <summary>差し替えが反映されなかった（または確認できない）整文結果。</summary>
    PolishedNotApplied,
}

public sealed record HeldText(HeldKind Kind, string Text)
{
    public override string ToString() => $"HeldText({Kind}, {Text.Length} chars)";
}

/// <summary>
/// Hane 連携（設計 §4.1 / §4.5 の W-1〜W-2 部分）。録音開始時の文脈取得、原文の先行挿入、整文結果の条件付き差し替えを、
/// 既存の状態機械へ足す。Hane への要求はすべてイベントとして戻し、ループスレッドの外では状態に触れない。
/// </summary>
public sealed partial class SessionCoordinator
{
    /// <summary>接続・認証・前後取得の予算（設計 §4.1 の初期値）。録音開始は待たせない。</summary>
    internal static readonly TimeSpan HaneContextBudget = TimeSpan.FromMilliseconds(150);

    /// <summary>整文ジョブの同時上限（設計 §4.5 の初期値）。満杯なら原文を優先して新規の整文を省く。</summary>
    internal const int MaxReplaceJobs = 2;

    /// <summary>保持した一件を自動で解放するまでの時間（設計は「期限切れで解放」とのみ定める。この値は本実装の選択）。</summary>
    internal static readonly TimeSpan HeldTtl = TimeSpan.FromMinutes(10);

    private sealed record HaneContextEvt(long Id, HaneResult<HaneContext> Result) : Evt;
    private sealed record HaneInsertedEvt(long Id, HaneResult<HaneInserted> Result) : Evt;
    private sealed record JobPolishedEvt(long JobId, PolishResult Result) : Evt;
    private sealed record JobReplacedEvt(long JobId, HaneResult<HaneReplaced> Result) : Evt;
    private sealed record JobExpiredEvt(long JobId) : Evt;
    private sealed record DiscardHeldEvt : Evt;
    private sealed record HeldExpiredEvt(long Seq) : Evt;

    private enum JobPhase { Polishing, Waiting, Replacing }

    /// <summary>挿入済みの原文に対する整文と差し替え。録音スロットとは独立で、セッション ID ではなくジョブ ID で管理する。</summary>
    private sealed class ReplaceJob(long id, HaneWindow window, IHaneClient client, HaneInserted inserted, string raw, PolishInput input, Func<bool>? allowContext)
    {
        public PolishInput Input { get; } = input;
        public Func<bool>? AllowContext { get; } = allowContext;
        public long Id { get; } = id;
        public HaneWindow Window { get; } = window;
        public IHaneClient Client { get; } = client;
        public HaneInserted Inserted { get; } = inserted;
        public string Raw { get; } = raw;
        public JobPhase Phase { get; set; } = JobPhase.Polishing;
        public string? Polished { get; set; }
        public Task? Sent { get; set; }
        public CancellationTokenSource Cts { get; } = new();
    }

    private readonly IHaneLink? _haneLink;
    private HaneSetup.Ready? _hane;            // 録音開始時に固定する Hane 連携の構え（Off・対象なしは null）
    private HaneWindow? _haneWindow;
    private CancellationTokenSource? _haneCts; // 文脈取得の予算
    private bool _haneFetching;
    private HaneContext? _haneCtx;             // 取得できた文脈。無ければこのセッションは従来経路
    private readonly List<ReplaceJob> _jobs = [];
    private readonly List<Task> _inflight = []; // 送出済みで確定待ちの差し替え
    private long _jobSeq;
    private HeldText? _held;
    private long _heldSeq;

    /// <summary>保持している一件（コピー・破棄できる）。</summary>
    public HeldText? Held => Volatile.Read(ref _held);

    public event Action<HeldText?>? HeldChanged;

    /// <summary>テスト・診断用: 整文・差し替え待ちのジョブ数。</summary>
    public int ActiveJobCount => Volatile.Read(ref _jobCount);

    private int _jobCount;

    /// <summary>保持している一件を破棄する（コピーした後も呼ぶ）。</summary>
    public void DiscardHeld() => Post(new DiscardHeldEvt());

    // ── 録音開始時の文脈取得（設計 §4.1）──

    private void StartHaneContext(IInjectTarget? target)
    {
        ClearHaneSession();
        if (_haneLink is null || target is not IWindowTarget w) return;
        HaneSetup setup;
        try { setup = _haneLink.Prepare(); }
        catch { return; }
        if (setup is not HaneSetup.Ready ready) return;

        var window = new HaneWindow(w.Hwnd, w.Pid);
        var cts = _haneCts = new CancellationTokenSource(HaneContextBudget);
        var token = cts.Token;
        var id = _sessionId;
        var request = new HaneContextRequest(PendingRangeHandles: PendingHandles(window));
        var inflight = _inflight.ToArray();
        _hane = ready;
        _haneWindow = window;
        _haneFetching = true;
        _ = Task.Run(async () =>
        {
            HaneResult<HaneContext> result;
            try
            {
                // 送出済みの差し替えの確定待ちは、前後取得と同じ予算に収める（直列化は呼び出し元のループスレッドが担う）。
                if (inflight.Length > 0)
                {
                    try { await Task.WhenAll(inflight).WaitAsync(token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { /* 予算切れ。取得側が Timeout を返す */ }
                }
                result = await ready.Client.GetContextAsync(window, request, token).ConfigureAwait(false);
            }
            catch
            {
                result = HaneResult<HaneContext>.Fail(HaneFailure.Protocol);
            }
            Post(new HaneContextEvt(id, result));
        });
    }

    private IReadOnlyList<string> PendingHandles(HaneWindow window) =>
        _jobs.Where(j => j.Window == window).Select(j => j.Inserted.RangeHandle)
            .TakeLast(HaneContextRequest.MaxPendingHandles).ToArray();

    private void OnHaneContext(HaneContextEvt e)
    {
        if (e.Id != _sessionId || !_haneFetching || _state is not (SessionState.Recording or SessionState.Recognizing)) return; // 取消・切替後の遅れた結果
        _haneFetching = false;
        DisposeHaneCts();
        if (e.Result.Ok) _haneCtx = e.Result.Value; // 失敗は静かに従来経路（認識完了時に使えなければ）
    }

    private void ClearHaneSession()
    {
        try { _haneCts?.Cancel(); } catch (ObjectDisposedException) { }
        DisposeHaneCts();
        _hane = null;
        _haneWindow = null;
        _haneFetching = false;
        _haneCtx = null;
    }

    private void DisposeHaneCts()
    {
        _haneCts?.Dispose();
        _haneCts = null;
    }

    /// <summary>認識完了時点で文脈があれば連携経路。未取得ならこのセッションは従来経路（取得は取り消す）。</summary>
    private bool UseHane()
    {
        if (_hane is not null && _haneCtx is not null) return true;
        ClearHaneSession();
        return false;
    }

    /// <summary>整文後（または整文なし）の最終投入。文脈があれば Hane へ一度だけ挿入し、無ければ従来の投入。</summary>
    private void StartDeliver(string text)
    {
        if (_hane is not null && _haneCtx is not null) StartHaneInsert(text, rawFirst: false);
        else StartInject(text);
    }

    // ── 挿入（設計 §4.5 の InsertingRaw / 通常の一回挿入）──

    private void StartHaneInsert(string text, bool rawFirst)
    {
        var client = _hane!.Client;
        var window = _haneWindow!;
        var context = _haneCtx!;
        var id = _sessionId;
        // 編集要求を発行したら取り消し不可。従来の投入先は使わない（連携で確定できなかった場合に自動で貼らない）。
        _pendingText = text;
        _target = null;
        SetState(rawFirst ? SessionState.InsertingRaw : SessionState.Inserting);
        _ = Task.Run(async () =>
        {
            HaneResult<HaneInserted> result;
            try { result = await client.InsertAsync(window, context, text, CancellationToken.None).ConfigureAwait(false); }
            catch { result = HaneResult<HaneInserted>.Fail(HaneFailure.Protocol, effect: HaneEffect.Unknown); }
            Post(new HaneInsertedEvt(id, result));
        });
    }

    private void OnHaneInserted(HaneInsertedEvt e)
    {
        if (e.Id != _sessionId || _state is not (SessionState.InsertingRaw or SessionState.Inserting)) return;
        var rawFirst = _state == SessionState.InsertingRaw;
        var text = _pendingText!;
        var r = e.Result;
        if (r.Ok)
        {
            _pendingText = null;
            // 原文の挿入が確定した時点で録音スロットを解放し、整文は別ジョブへ移す。
            if (rawFirst) StartReplaceJob(_haneWindow!, _hane!.Client, r.Value!, text);
            SetState(SessionState.Idle);
            return;
        }
        if (r.Effect == HaneEffect.Unknown)
        {
            // 適用されたかもしれない。自動投入・再試行へは渡さず、コピーか破棄だけを提示する。
            _pendingText = null;
            Hold(HeldKind.InsertUnconfirmed, text);
            Notify(NoticeKind.Warning, "入力結果を確認できません。Hane の本文を確認してください");
            SetState(SessionState.Idle);
            return;
        }
        // 未適用が確かな拒否。文脈を取った後は対象・選択を再確認できないので、自動では従来入力へ切り替えない。
        // 未投入の一件を保持し、利用者の明示再試行（その時点の前景へ従来入力）か破棄に委ねる。
        Notify(NoticeKind.Error, DescribeInsertRejection(r));
        SetState(SessionState.Failed);
    }

    private static string DescribeInsertRejection(HaneResult<HaneInserted> r)
    {
        var reason = r.Failure == HaneFailure.TooLarge ? "テキストが長すぎます"
            : r.Code switch
            {
                "STALE_CURSOR" or "STALE_VERSION" => "録音中にカーソルか文書が変わりました",
                "STALE_TARGET" => "入力先のタブかウィンドウが変わりました",
                "BUSY" or "NOT_EDITABLE" => "Hane が入力を受け付けられない状態です",
                _ => null,
            };
        return reason is null ? "Hane に入力できませんでした" : $"Hane に入力できませんでした（{reason}）";
    }

    // ── 整文ジョブと条件付き差し替え ──

    private void StartReplaceJob(HaneWindow window, IHaneClient client, HaneInserted inserted, string raw)
    {
        switch (_polish)
        {
            case PolishSetup.Ready ready:
                if (_jobs.Count >= MaxReplaceJobs)
                {
                    Notify(NoticeKind.Info, "整文の処理が混み合っているため、原文のままにしました");
                    return;
                }
                var job = new ReplaceJob(++_jobSeq, window, client, inserted, raw, BuildPolishInput(raw), _hane?.ContextAllowed);
                _jobs.Add(job);
                SyncJobCount();
                RunJobPolish(job, ready);
                break;
            case PolishSetup.Unconfigured u:
                Notify(NoticeKind.Warning, $"整文の設定が未完了のため、原文のままです（{u.Reason}）");
                break;
        }
    }

    private void RunJobPolish(ReplaceJob job, PolishSetup.Ready ready)
    {
        var token = job.Cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                // 整文側が期限を守らなくても、期限で原文維持へ進む（取消は結果を捨てる）。
                var work = Task.Run(() => RunPolish(ready, job.Input, job.AllowContext, token), CancellationToken.None);
                _ = work.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                var winner = await Task.WhenAny(work, Task.Delay(ready.Timeout, token)).ConfigureAwait(false);
                Post(new JobPolishedEvt(job.Id, winner == work
                    ? await work.ConfigureAwait(false)
                    : PolishResult.Fallback(PolishFailure.Timeout)));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // 整文だけの取消。原文はそのまま残る。
            }
            catch
            {
                Post(new JobPolishedEvt(job.Id, PolishResult.Fallback(PolishFailure.Error)));
            }
        });
        // 範囲ハンドルの失効（既定 120 秒）。保留したまま過ぎたら原文を維持する。
        var remaining = TimeSpan.FromMilliseconds(Math.Max(0, job.Inserted.ExpiresAtTick - Environment.TickCount64));
        _ = Task.Delay(remaining, token).ContinueWith(t => { if (!t.IsCanceled) Post(new JobExpiredEvt(job.Id)); }, TaskScheduler.Default);
    }

    // ── 文脈付き整文（設計 §4.1 / §4.3 / §4.6）──

    /// <summary>
    /// 整文の入力。Hane の文脈があり、文脈送信が許可されていれば文脈つき、そうでなければ従来の発話だけ。
    /// 文脈の取得に成功しても、許可が OFF・文脈が空のときは何も足さない（従来経路と同一の要求になる）。
    /// </summary>
    private PolishInput BuildPolishInput(string raw)
    {
        if (_hane is { } h && _haneCtx is { } ctx && h.ContextAllowed())
        {
            try { return new PolishInput(raw, PolishContextBuilder.Build(ctx)); }
            catch { /* 文脈の組み立てに失敗しても整文は発話だけで続ける */ }
        }
        return new PolishInput(raw);
    }

    /// <summary>送信の直前にもう一度許可を確かめ、OFF になっていれば文脈を外して従来の整文にする。</summary>
    private static Task<PolishResult> RunPolish(PolishSetup.Ready ready, PolishInput input, Func<bool>? allowContext, CancellationToken ct)
    {
        if (input.Context is not null && allowContext?.Invoke() != true) input = new PolishInput(input.Utterance);
        return ready.RunAsync(input, ct);
    }

    private void OnJobPolished(JobPolishedEvt e)
    {
        var job = FindJob(e.JobId);
        if (job is null || job.Phase != JobPhase.Polishing) return;
        var r = e.Result;
        if (!r.IsSuccess || string.IsNullOrWhiteSpace(r.Text))
        {
            Notify(NoticeKind.Warning, "整文できなかったため原文を残しました");
            RemoveJob(job);
            return;
        }
        var polished = r.Text.Trim();
        if (polished == job.Raw)
        {
            RemoveJob(job); // 差がなければ編集しない
            return;
        }
        job.Polished = polished;
        job.Phase = JobPhase.Waiting; // 送出は PumpJobs が、同じ文書の録音・認識が終わってから行う
    }

    /// <summary>
    /// 差し替えの送出。同じ文書に録音・認識中のセッションがある間は、直近の挿入確定後（挿入なしで終了した場合はその時点）まで保留する。
    /// 文書が未確定の取得中も同じ文書として待つ。競合の最終判定は Hane の版・範囲検証に従う。
    /// </summary>
    private void PumpJobs()
    {
        if (_jobs.Count == 0) return;
        foreach (var job in _jobs.Where(j => j.Phase == JobPhase.Waiting).ToArray())
        {
            if (SessionBlocksReplace(job)) continue;
            DispatchReplace(job);
        }
    }

    private bool SessionBlocksReplace(ReplaceJob job)
    {
        if (_state is SessionState.Idle or SessionState.Failed || _hane is null) return false; // 従来経路のセッションは文書に触れない
        if (_haneFetching) return true;
        return _haneCtx is { } c && c.InstanceId == job.Inserted.InstanceId && c.DocumentId == job.Inserted.DocumentId;
    }

    private void DispatchReplace(ReplaceJob job)
    {
        job.Phase = JobPhase.Replacing;
        if (Environment.TickCount64 > job.Inserted.ExpiresAtTick)
        {
            FinishNotApplied(job, effectUnknown: false);
            return;
        }
        var polished = job.Polished!;
        job.Sent = Task.Run(async () =>
        {
            HaneResult<HaneReplaced> result;
            try { result = await job.Client.ReplaceAsync(job.Window, job.Inserted, polished, CancellationToken.None).ConfigureAwait(false); }
            catch { result = HaneResult<HaneReplaced>.Fail(HaneFailure.Protocol, effect: HaneEffect.Unknown); }
            Post(new JobReplacedEvt(job.Id, result));
        });
        _inflight.Add(job.Sent);
    }

    private void OnJobReplaced(JobReplacedEvt e)
    {
        var job = FindJob(e.JobId);
        if (job is null) return;
        if (job.Sent is not null) _inflight.Remove(job.Sent);
        if (e.Result.Ok)
        {
            RemoveJob(job); // 反映された。追加の通知はしない
            return;
        }
        FinishNotApplied(job, e.Result.Effect == HaneEffect.Unknown);
    }

    /// <summary>差し替えが反映されなかった（または確認できない）。再送・追加の貼り付け・原文を戻す編集は行わず、結果の明示コピーだけを提供する。</summary>
    private void FinishNotApplied(ReplaceJob job, bool effectUnknown)
    {
        RemoveJob(job);
        Hold(HeldKind.PolishedNotApplied, job.Polished!);
        Notify(effectUnknown ? NoticeKind.Warning : NoticeKind.Info,
            effectUnknown ? "整文の反映を確認できません" : "整文結果は反映されませんでした");
    }

    private void OnJobExpired(JobExpiredEvt e)
    {
        var job = FindJob(e.JobId);
        if (job is null || job.Phase == JobPhase.Replacing) return;
        if (job.Polished is not null) FinishNotApplied(job, effectUnknown: false);
        else RemoveJob(job);
    }

    /// <summary>原文確定後の整文中は整文だけを取り消す。原文は利用者の Undo に委ね、送出済みの差し替えは取り消さない。</summary>
    private void CancelPolishJobs()
    {
        foreach (var job in _jobs.Where(j => j.Phase != JobPhase.Replacing).ToArray()) RemoveJob(job);
    }

    private ReplaceJob? FindJob(long id) => _jobs.FirstOrDefault(j => j.Id == id);

    private void RemoveJob(ReplaceJob job)
    {
        _jobs.Remove(job);
        try { job.Cts.Cancel(); } catch (ObjectDisposedException) { }
        SyncJobCount();
    }

    private void SyncJobCount() => Volatile.Write(ref _jobCount, _jobs.Count);

    // ── 保持した一件 ──

    private void Hold(HeldKind kind, string text)
    {
        var held = new HeldText(kind, text);
        Volatile.Write(ref _held, held);
        var seq = ++_heldSeq;
        HeldChanged?.Invoke(held);
        _ = Task.Delay(HeldTtl).ContinueWith(_ => Post(new HeldExpiredEvt(seq)), TaskScheduler.Default);
    }

    private void ReleaseHeld()
    {
        if (Volatile.Read(ref _held) is null) return;
        Volatile.Write(ref _held, null);
        HeldChanged?.Invoke(null);
    }

    private void ShutdownHane()
    {
        try { _haneCts?.Cancel(); } catch (ObjectDisposedException) { }
        foreach (var job in _jobs.ToArray()) RemoveJob(job);
    }
}
