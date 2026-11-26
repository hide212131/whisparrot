using System.Diagnostics;
using System.Threading.Channels;
using Whisparrot.Core.Hane;
using Whisparrot.Core.Settings;

namespace Whisparrot.Core;

/// <summary>
/// ストリーミング入力（docs/design-streaming.md の S-2）。録音を続けたまま固定秒数の区間を確定し、
/// 区間ごとに認識 → 整文 → 投入を重ねて進める。録音スロットはセッションに一つ、区間は <c>(sessionId, sequence)</c> で識別し、
/// 各段の完了は識別子付きのイベントとして既存のイベントループへ戻す（終端済みの区間・取消済みのセッションの遅い完了は捨てる）。
/// 投入は「次に扱う連番」からしか送出しない（後続が先に完了しても追い越さない）。ストリーミング OFF のときこのファイルの経路は一切動かない。
/// </summary>
public sealed partial class SessionCoordinator
{
    private sealed record SegmentCutEvt(long Id, AudioSegment Segment) : Evt;
    private sealed record IntakeEndedEvt(long Id) : Evt;
    private sealed record RecordingLimitEvt(long Id) : Evt;
    private sealed record StreamStopFailedEvt(long Id) : Evt;
    private sealed record SegRecognizedEvt(long Id, int Seq, RecognitionResult? Result, Exception? Error) : Evt;
    private sealed record SegPolishedEvt(long Id, int Seq, PolishResult Result) : Evt;
    private sealed record SegInjectedEvt(long Id, int Seq, InjectResult Result) : Evt;
    private sealed record StreamTickEvt(long Id) : Evt;

    private enum SegState { Queued, Recognizing, AwaitPolish, Polishing, Ready, Injecting, Done, Dropped, Silent }

    private sealed class Seg(int seq, long confirmedTs, PcmAudio? pcm)
    {
        public int Seq { get; } = seq;
        public long ConfirmedTs { get; } = confirmedTs;
        public SegState State { get; set; } = SegState.Queued;
        public PcmAudio? Pcm { get; set; } = pcm;
        public string? Raw { get; set; }
        public string? Final { get; set; }
        public bool RawFallback { get; set; }   // 整文を使う設定で、整文できず原文を確定した
        public long PolishDeadlineTs { get; set; }
        public CancellationTokenSource? Cts { get; set; }
        public string? Code { get; set; }        // エンジンモード: 欠落（Dropped）の理由コード
        public string? PolishCode { get; set; }  // エンジンモード: 原文へ戻った理由コード
        public bool RawSent { get; set; }
        public bool PolishSent { get; set; }
    }

    private sealed class StreamRun(long id, CancellationTokenSource cts, RecognitionSetup.Ready recognize, PolishSetup.Ready? polish, IInjectTarget target, StreamingLimits limits)
    {
        public long Id { get; } = id;
        public CancellationTokenSource Cts { get; } = cts;
        public RecognitionSetup.Ready Recognize { get; } = recognize;
        public PolishSetup.Ready? Polish { get; } = polish;
        public IInjectTarget Target { get; } = target;
        public StreamingLimits Limits { get; } = limits;
        public IStreamingRecorder Recorder { get; init; } = null!;

        /// <summary>エンジンモード（<see cref="SessionCoordinator.BeginCapture"/>）のときだけ非 null。区間結果は投入ではなくここへ出す。</summary>
        public ISegmentSink? Sink { get; init; }
        public PolishContext? Context { get; init; }
        public int SentSeq { get; set; }
        public SortedDictionary<int, Seg> Segs { get; } = [];
        public int NextSeq { get; set; } = 1;
        public int LastSeq { get; set; }
        public int RecRunning { get; set; }
        public int PolishRunning { get; set; }
        public bool Injecting { get; set; }
        public bool IntakeEnded { get; set; }
        public long FlushDeadlineTs { get; set; }
        public long SendDeadlineTs { get; set; }
        public char? LastChar { get; set; }
        public int Injected { get; set; }
        public int RawInjected { get; set; }
        public int Dropped { get; set; }
        public int Unconfirmed { get; set; }
        public int Empty { get; set; }
        public int Discarded { get; set; }
        public int ConsecRaw { get; set; }
        public int ConsecDropped { get; set; }
        public bool NotifiedRaw { get; set; }
        public bool NotifiedDropped { get; set; }
    }

    private readonly Func<StreamingSettings>? _streamingSettings;
    private StreamRun? _stream;

    /// <summary>ストリーミングの並行度・期限・上限。既定は設計値で、テストだけが差し替える。録音開始の前に設定すること。</summary>
    internal StreamingLimits StreamingLimits { get; set; } = new();

    // ── 開始 ──

    /// <summary>
    /// ストリーミングで録音を始める。条件（設定 ON・連続録音に対応した録音・クラウド認識・Hane 連携が無効）を満たさなければ false で、
    /// 従来の一括録音へ進む（一括の挙動は変えない）。条件を満たしたうえで開始できなかったときは、通知して true を返す（一括へ落とさない）。
    /// </summary>
    private bool TryBeginStreaming(RecognitionSetup.Ready recognize, IInjectTarget? target)
    {
        StreamingSettings cfg;
        try { cfg = _streamingSettings?.Invoke() ?? StreamingSettings.Default; }
        catch { return false; }
        if (!cfg.Enabled) return false;
        if (_recorder is not IStreamingRecorder streaming) return false;
        if (recognize.Engine != RecognitionEngine.Cloud)
        {
            Notify(NoticeKind.Info, "ストリーミング入力はクラウド認識のときだけ使えます。今回は一括で入力します");
            return false;
        }
        if (HaneLinkReady())
        {
            Notify(NoticeKind.Info, "Hane 連携が有効なため、今回は一括で入力します（ストリーミングは Hane に未対応）");
            return false;
        }
        if (target is null)
        {
            Notify(NoticeKind.Error, "ストリーミング入力の入力先を記録できませんでした。入力先のウィンドウを選んでから始めてください");
            return true;
        }

        var polish = PreparePolish();
        var limits = StreamingLimits;
        ChannelReader<PcmFrame> reader;
        try
        {
            reader = streaming.StartStreaming();
        }
        catch (Exception ex)
        {
            SafeCancelRecorder();
            Notify(NoticeKind.Error, ex is RecorderException ? ex.Message : "マイクを開始できませんでした");
            return true;
        }

        _stopping = false;
        _recognize = recognize;
        _target = null;
        _polish = polish;
        if (polish is PolishSetup.Unconfigured u)
            Notify(NoticeKind.Warning, $"整文の設定が未完了のため、そのまま入力します（{u.Reason}）");
        var run = new StreamRun(_sessionId, new CancellationTokenSource(), recognize, polish as PolishSetup.Ready, target, limits) { Recorder = streaming };
        _stream = run;
        SetState(SessionState.Recording);
        _ = Task.Run(() => PumpAsync(run, reader, SegmentSplitter.ForSeconds(cfg.IntervalSeconds)));
        _ = Task.Run(() => TickAsync(run));
        return true;
    }

    private bool HaneLinkReady()
    {
        if (_haneLink is null) return false;
        try { return _haneLink.Prepare() is HaneSetup.Ready; }
        catch { return false; }
    }

    /// <summary>連続 PCM を区間へ切る。区間の確定とチャンネルの完了（停止・切断・取消）をイベントとして戻す。</summary>
    private async Task PumpAsync(StreamRun run, ChannelReader<PcmFrame> reader, SegmentSplitter splitter)
    {
        var maxSamples = (long)(run.Limits.MaxRecording.TotalSeconds * PcmAudio.SampleRate);
        var limitHit = false;
        try
        {
            await foreach (var frame in reader.ReadAllAsync(run.Cts.Token).ConfigureAwait(false))
            {
                foreach (var segment in splitter.Push(frame.StartSample, frame.Samples)) Post(new SegmentCutEvt(run.Id, segment));
                if (!limitHit && splitter.Position >= maxSamples)
                {
                    limitHit = true;
                    Post(new RecordingLimitEvt(run.Id));
                }
            }
        }
        catch (OperationCanceledException)
        {
            return; // 取消。結果は使われない
        }
        if (splitter.Flush() is { } tail) Post(new SegmentCutEvt(run.Id, tail));
        Post(new IntakeEndedEvt(run.Id));
    }

    private async Task TickAsync(StreamRun run)
    {
        try
        {
            using var timer = new PeriodicTimer(run.Limits.Tick);
            while (await timer.WaitForNextTickAsync(run.Cts.Token).ConfigureAwait(false)) Post(new StreamTickEvt(run.Id));
        }
        catch (OperationCanceledException) { }
    }

    // ── 停止・取消・録音側の事象 ──

    private void BeginStreamStop()
    {
        var run = _stream!;
        if (_stopping) return;
        _stopping = true;
        SetState(SessionState.Flushing);
        _ = Task.Run(async () =>
        {
            try { await run.Recorder.StopStreamingAsync().ConfigureAwait(false); }
            catch { Post(new StreamStopFailedEvt(run.Id)); }
        });
    }

    private void OnStreamStopFailed(StreamStopFailedEvt e)
    {
        if (_stream is not { } run || run.Id != e.Id) return;
        Notify(NoticeKind.Error, "録音を完了できませんでした。ここまでの区間は入力します");
        SafeCancelRecorder(); // チャンネルを完了させる。回収できた区間は流す
    }

    private void OnRecordingLimit(RecordingLimitEvt e)
    {
        if (_stream is not { } run || run.Id != e.Id || _state != SessionState.Recording) return;
        Notify(NoticeKind.Warning, $"録音は 1 回 {run.Limits.MaxRecording.TotalMinutes:0} 分までです。ここまでを入力します");
        BeginStreamStop();
    }

    private void OnStreamRecorderFailed(RecorderError error)
    {
        if (_stream is { Sink: { } sink } engineRun)
        {
            // エンジンモード: 継続できない障害。未確定分は Hane が破棄するので、ここで打ち切って失敗で終える。
            SafeCancelRecorder();
            _stopping = false;
            _stream = null;
            CancelRun(engineRun);
            _sessionId++;
            sink.SessionEnded(EndStatus.Failed, engineRun.SentSeq, VoiceCodes.MicrophoneLost);
            SetState(SessionState.Idle);
            return;
        }
        // マイク切断: デバイスは解放済みで、チャンネルは完了する。回収できた区間と確定済みの区間はフラッシュする。
        _stopping = false;
        Notify(NoticeKind.Error, error.Message);
        if (_state == SessionState.Recording) SetState(SessionState.Flushing);
    }

    private void CancelStream()
    {
        var run = _stream!;
        SafeCancelRecorder();
        _stopping = false;
        var injecting = run.Injecting;
        _stream = null;
        CancelRun(run);
        _sessionId++;
        run.Sink?.SessionEnded(EndStatus.Cancelled, run.SentSeq, null);
        Notify(NoticeKind.Info, injecting
            ? "取消しました。入力済みの内容は残ります（送信済みの入力は反映される場合があります）"
            : "取消しました。入力済みの内容は残ります");
        SetState(SessionState.Idle);
    }

    /// <summary>実行中の認識・整文・投入と、未出力の PCM・テキストを捨てる。</summary>
    private static void CancelRun(StreamRun run)
    {
        try { run.Cts.Cancel(); } catch (ObjectDisposedException) { }
        foreach (var s in run.Segs.Values)
        {
            s.Pcm = null;
            s.Raw = null;
            s.Final = null;
        }
        run.Segs.Clear();
    }

    private void AbortStreamOnError(bool report = false)
    {
        if (_stream is not { } run) return;
        _stream = null;
        CancelRun(run);
        if (report) run.Sink?.SessionEnded(EndStatus.Failed, run.SentSeq, VoiceCodes.EngineFailed);
    }

    // ── 区間の確定 ──

    private void OnSegmentCut(SegmentCutEvt e)
    {
        if (_stream is not { } run || run.Id != e.Id) return;
        if (run.Sink is not null && e.Segment.Sequence > run.Limits.MaxSegments) return; // 上限を超えた区間は連番を割り当てない
        var seg = new Seg(e.Segment.Sequence, Stopwatch.GetTimestamp(), e.Segment.Pcm);
        run.LastSeq = Math.Max(run.LastSeq, seg.Seq);
        run.Segs[seg.Seq] = seg;
        var live = run.Segs.Values.Count(s => s != seg && s.State is not (SegState.Done or SegState.Dropped or SegState.Silent));
        if (live >= run.Limits.MaxBufferedSegments) MarkDropped(run, seg, VoiceCodes.Overloaded); // 滞留の上限。連番は割り当て、PCM は解放する
        ScheduleStream(run);
    }

    private void OnIntakeEnded(IntakeEndedEvt e)
    {
        if (_stream is not { } run || run.Id != e.Id) return;
        run.IntakeEnded = true;
        run.FlushDeadlineTs = Stopwatch.GetTimestamp() + Ticks(run.Limits.Flush);
        run.SendDeadlineTs = run.FlushDeadlineTs + Ticks(run.Limits.SendWindow);
        _stopping = false;
        if (_state == SessionState.Recording) SetState(SessionState.Flushing);
        ScheduleStream(run);
    }

    private void OnStreamTick(StreamTickEvt e)
    {
        if (_stream is { } run && run.Id == e.Id) ScheduleStream(run);
    }

    // ── スケジューラー（イベントごとに一度。ループスレッドだけが呼ぶ）──

    private static long Ticks(TimeSpan t) => (long)(t.TotalSeconds * Stopwatch.Frequency);

    private static bool Reached(long now, long originTs, TimeSpan limit) => now - originTs >= Ticks(limit);

    private void ScheduleStream(StreamRun run)
    {
        if (run.Sink is not null) { ScheduleEngine(run); return; }
        CheckDeadlines(run);
        AdvanceOutput(run);
        StartPolishes(run);
        StartRecognitions(run);
        TryFinish(run);
    }

    private void CheckDeadlines(StreamRun run)
    {
        var now = Stopwatch.GetTimestamp();
        var flushing = run.FlushDeadlineTs != 0 && now >= run.FlushDeadlineTs;
        var sendOver = run.SendDeadlineTs != 0 && now >= run.SendDeadlineTs;
        foreach (var s in run.Segs.Values.ToArray())
        {
            switch (s.State)
            {
                case SegState.Queued or SegState.Recognizing:
                    // 期限後も枠は呼び出しが終わるまで解放しない（完了イベントで解放する）。
                    if (flushing || Reached(now, s.ConfirmedTs, run.Limits.RecognitionDeadline))
                    {
                        CancelSeg(s);
                        MarkDropped(run, s, VoiceCodes.Timeout);
                    }
                    break;
                case SegState.AwaitPolish:
                    if (flushing || Reached(now, s.ConfirmedTs, run.Limits.PolishCutoff)) UseRaw(run, s, VoiceCodes.Timeout);
                    break;
                case SegState.Polishing:
                    if (flushing || now >= s.PolishDeadlineTs)
                    {
                        CancelSeg(s);
                        UseRaw(run, s, VoiceCodes.Timeout);
                    }
                    break;
                case SegState.Ready when run.Sink is null: // エンジンモードに出力枠待ちはない（順序は原文の送出側で保つ）
                    if (sendOver || Reached(now, s.ConfirmedTs, run.Limits.OutputWait)) MarkDropped(run, s);
                    break;
            }
        }
    }

    private static void CancelSeg(Seg s)
    {
        try { s.Cts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private void StartRecognitions(StreamRun run)
    {
        var limit = run.Recognize.Engine == RecognitionEngine.Cloud ? run.Limits.CloudRecognitionConcurrency : 1;
        foreach (var s in run.Segs.Values)
        {
            if (run.RecRunning >= limit) break;
            if (s.State != SegState.Queued) continue;
            var pcm = s.Pcm!;
            var cts = s.Cts = CancellationTokenSource.CreateLinkedTokenSource(run.Cts.Token);
            s.State = SegState.Recognizing;
            run.RecRunning++;
            var (id, seq, token) = (run.Id, s.Seq, cts.Token);
            _ = Task.Run(async () =>
            {
                try
                {
                    Post(new SegRecognizedEvt(id, seq, await run.Recognize.RecognizeAsync(new RecognitionRequest(pcm), token).ConfigureAwait(false), null));
                }
                catch (Exception ex)
                {
                    Post(new SegRecognizedEvt(id, seq, null, ex));
                }
                finally
                {
                    cts.Dispose(); // 呼び出しが終わってから破棄する（期限後の取消は CancelSeg が ObjectDisposed を許容する）
                }
            });
        }
    }

    private void StartPolishes(StreamRun run)
    {
        if (run.Polish is not { } polish) return;
        foreach (var s in run.Segs.Values)
        {
            if (run.PolishRunning >= run.Limits.PolishConcurrency) break;
            if (s.State != SegState.AwaitPolish) continue;
            var now = Stopwatch.GetTimestamp();
            // 整文は開始から Timeout、かつ区間確定から PolishCutoff 以内（枠待ちも含む）。
            var deadline = Math.Min(now + Ticks(polish.Timeout), s.ConfirmedTs + Ticks(run.Limits.PolishCutoff));
            if (deadline <= now) { UseRaw(run, s, VoiceCodes.Timeout); continue; }
            s.PolishDeadlineTs = deadline;
            var cts = s.Cts = CancellationTokenSource.CreateLinkedTokenSource(run.Cts.Token);
            cts.CancelAfter(TimeSpan.FromSeconds((double)(deadline - now) / Stopwatch.Frequency));
            s.State = SegState.Polishing;
            run.PolishRunning++;
            var (id, seq, raw, token) = (run.Id, s.Seq, s.Raw!, cts.Token);
            _ = Task.Run(async () =>
            {
                try
                {
                    Post(new SegPolishedEvt(id, seq, await polish.RunAsync(new PolishInput(raw, run.Context), token).ConfigureAwait(false)));
                }
                catch
                {
                    Post(new SegPolishedEvt(id, seq, PolishResult.Fallback(token.IsCancellationRequested ? PolishFailure.Timeout : PolishFailure.Error)));
                }
                finally
                {
                    cts.Dispose();
                }
            });
        }
    }

    /// <summary>連番順の出力。先頭が出力済み・欠落・無音なら進め、投入できる状態なら一件だけ送出する。後続が先に終わっても追い越さない。</summary>
    private void AdvanceOutput(StreamRun run)
    {
        while (run.Segs.TryGetValue(run.NextSeq, out var s))
        {
            switch (s.State)
            {
                case SegState.Done or SegState.Dropped or SegState.Silent:
                    run.Segs.Remove(s.Seq);
                    run.NextSeq++;
                    continue;
                case SegState.Ready when !run.Injecting:
                    StartInject(run, s);
                    return;
                default:
                    return;
            }
        }
    }

    private void StartInject(StreamRun run, Seg s)
    {
        var text = SegmentJoin.Separator(run.LastChar, s.Final!) + s.Final!;
        s.State = SegState.Injecting;
        run.Injecting = true;
        var (id, seq, token) = (run.Id, s.Seq, run.Cts.Token);
        var target = run.Target;
        _ = Task.Run(async () =>
        {
            try
            {
                Post(new SegInjectedEvt(id, seq, await _injector.InjectSegmentAsync(text, target, token).ConfigureAwait(false)));
            }
            catch (Exception ex)
            {
                Post(new SegInjectedEvt(id, seq, new InjectResult(InjectOutcome.Failed, ex.Message)));
            }
        });
    }

    // ── 各段の完了 ──

    private void OnSegRecognized(SegRecognizedEvt e)
    {
        if (_stream is not { } run || run.Id != e.Id) return;
        run.RecRunning--; // 呼び出しが実際に終わった時点で枠を返す
        if (run.Segs.TryGetValue(e.Seq, out var s) && s.State == SegState.Recognizing)
        {
            s.Pcm = null;
            var text = e.Result?.Text.Trim() ?? "";
            if (e.Error is not null) MarkDropped(run, s);
            else if (run.Sink is not null && System.Text.Encoding.UTF8.GetByteCount(text) > MaxSegmentTextBytes) MarkDropped(run, s);
            else if (text.Length == 0)
            {
                s.State = SegState.Silent; // 空の結果は欠落とは別に数える（固定分割では無音かどうか判別できない）
                run.Empty++;
                run.ConsecDropped = 0;
            }
            else
            {
                BackendReported?.Invoke(e.Result!.Backend);
                s.Raw = text;
                if (run.Polish is null) { s.Final = text; s.State = SegState.Ready; }
                else s.State = SegState.AwaitPolish;
            }
        }
        ScheduleStream(run);
    }

    private void OnSegPolished(SegPolishedEvt e)
    {
        if (_stream is not { } run || run.Id != e.Id) return;
        run.PolishRunning--;
        if (run.Segs.TryGetValue(e.Seq, out var s) && s.State == SegState.Polishing)
        {
            if (e.Result.IsSuccess && !string.IsNullOrWhiteSpace(e.Result.Text)
                && (run.Sink is null || System.Text.Encoding.UTF8.GetByteCount(e.Result.Text.Trim()) <= MaxSegmentTextBytes))
            {
                s.Final = e.Result.Text.Trim();
                s.State = SegState.Ready;
            }
            else UseRaw(run, s, e.Result.IsSuccess || e.Result.Failure != PolishFailure.Timeout ? VoiceCodes.PolishFailed : VoiceCodes.Timeout);
        }
        ScheduleStream(run);
    }

    private void OnSegInjected(SegInjectedEvt e)
    {
        if (_stream is not { } run || run.Id != e.Id) return;
        run.Injecting = false;
        if (!run.Segs.TryGetValue(e.Seq, out var s) || s.State != SegState.Injecting) { ScheduleStream(run); return; }

        if (e.Result.Outcome == InjectOutcome.Success)
        {
            s.State = SegState.Done;
            run.Injected++;
            run.LastChar = s.Final![^1];
            run.ConsecDropped = 0;
            if (s.RawFallback)
            {
                run.RawInjected++;
                if (++run.ConsecRaw >= 3 && !run.NotifiedRaw)
                {
                    run.NotifiedRaw = true;
                    Notify(NoticeKind.Warning, "整文できない区間が続いています。原文のまま入力しています");
                }
            }
            else run.ConsecRaw = 0;
            ScheduleStream(run);
            return;
        }

        // 入力先の不一致・送出の失敗・適用不明: 以降の自動投入を止め、未投入または未確認の直近一件だけを保持する。
        Notify(NoticeKind.Error, e.Result.EffectUnknown
            ? "入力できたか確認できないため、ストリーミング入力を止めました。入力先を確認してください"
            : (e.Result.Message ?? "入力できませんでした") + "。ストリーミング入力を止めました");
        if (e.Result.EffectUnknown) run.Unconfirmed++; else run.Dropped++;
        Hold(HeldKind.StreamingNotInjected, s.Final!);
        run.Segs.Remove(s.Seq);
        AbortRun(run);
    }

    /// <summary>自動投入を止める。録音・認識・整文と未出力の区間を打ち切り、集計を知らせて待機へ戻る。</summary>
    private void AbortRun(StreamRun run)
    {
        SafeCancelRecorder();
        _stopping = false;
        run.Discarded += run.Segs.Values.Count(s => s.State is not (SegState.Done or SegState.Dropped or SegState.Silent));
        _stream = null;
        CancelRun(run);
        _sessionId++;
        Notify(NoticeKind.Info, Summary(run));
        SetState(SessionState.Idle);
    }

    // ── 区間の確定結果 ──

    private void MarkDropped(StreamRun run, Seg s, string code = VoiceCodes.RecognitionFailed)
    {
        s.State = SegState.Dropped;
        s.Code = code;
        s.Pcm = null;
        s.Raw = null;
        run.Dropped++;
        run.ConsecRaw = 0;
        if (++run.ConsecDropped >= 3 && !run.NotifiedDropped)
        {
            run.NotifiedDropped = true;
            Notify(NoticeKind.Warning, "認識できない区間が続いています");
        }
    }

    /// <summary>整文できなかった（時間切れ・失敗・空）ので、認識原文を出力候補にする。</summary>
    private static void UseRaw(StreamRun run, Seg s, string code = VoiceCodes.PolishFailed)
    {
        s.PolishCode = code;
        s.Final = s.Raw;
        s.RawFallback = true;
        s.State = SegState.Ready;
    }

    private void TryFinish(StreamRun run)
    {
        if (!run.IntakeEnded || run.Injecting || run.NextSeq <= run.LastSeq) return;
        _stream = null;
        try { run.Cts.Cancel(); } catch (ObjectDisposedException) { }
        Notify(NoticeKind.Info, run.LastSeq == 0 ? "音声がありませんでした" : Summary(run));
        SetState(SessionState.Idle);
    }

    /// <summary>完了時の集約通知（設計 §6）。原文件数は入力済み件数の内訳。取消による破棄・空の結果は別に数える。</summary>
    private static string Summary(StreamRun run)
    {
        var text = $"入力 {run.Injected} 区間・原文のまま {run.RawInjected} 区間・欠落 {run.Dropped} 区間";
        if (run.Unconfirmed > 0) text += $"・適用未確認 {run.Unconfirmed} 区間";
        if (run.Empty > 0) text += $"・空 {run.Empty} 区間";
        if (run.Discarded > 0) text += $"・取消 {run.Discarded} 区間";
        return text;
    }
}
