using Whisparrot.Core.Settings;

namespace Whisparrot.Core;

/// <summary>
/// エンジンモード（docs/design-voice-composition.md の P2）。ストリーミングの区間経路（連続録音 → 固定秒数の区間 → 認識 → 整文）をそのまま使い、
/// 区間結果を貼り付けではなく <see cref="ISegmentSink"/> へ出す。原文は区間順にそろえて整文の開始より前に送り、整文は対応する原文の後に区間順を問わず送る。
/// 投入・前景取得・クリップボードは通らない。
/// </summary>
public sealed partial class SessionCoordinator
{
    private sealed record EngineBeginEvt(ISegmentSink Sink, PolishContext? Context) : Evt;

    /// <summary>区間テキストの上限（UTF-8、設計 §2.1）。超えた原文は認識失敗、超えた整文は整文失敗として扱う。</summary>
    internal const int MaxSegmentTextBytes = 32 * 1024;

    /// <summary>エンジンモードで区間経路に使える認識方式。クラウド以外は実時間性能とワーカー回収の検証が済むまで対応としない（設計 P2）。</summary>
    private static bool EngineSupports(RecognitionEngine engine) => engine == RecognitionEngine.Cloud;

    /// <summary>
    /// エンジンモードで録音を始める。待機中でなければ、または開始できなければ <see cref="ISegmentSink.SessionEnded"/>（<c>session.start</c> なし）で失敗を返す。
    /// 停止は <see cref="Stop"/>、取消は <see cref="Cancel"/>。ストリーミング設定の ON/OFF には従わず、区間の秒数だけを使う。
    /// </summary>
    /// <param name="context">録音開始時の文書文脈。文脈の送信許可は呼び出し側（Hane）が判断済みで、渡されたものを設定済みの整文へだけ載せる。</param>
    public void BeginCapture(ISegmentSink sink, PolishContext? context = null) => Post(new EngineBeginEvt(sink, context));

    private void OnEngineBegin(EngineBeginEvt e)
    {
        var sink = e.Sink;
        if (_state != SessionState.Idle) { sink.SessionEnded(EndStatus.Failed, 0, VoiceCodes.EngineFailed); return; }

        var setup = PrepareRecognition();
        if (setup is not RecognitionSetup.Ready recognize || !EngineSupports(recognize.Engine))
        {
            sink.SessionEnded(EndStatus.Failed, 0, VoiceCodes.RecognitionFailed);
            return;
        }
        if (_recorder is not IStreamingRecorder streaming)
        {
            sink.SessionEnded(EndStatus.Failed, 0, VoiceCodes.EngineFailed);
            return;
        }

        StreamingSettings cfg;
        try { cfg = _streamingSettings?.Invoke() ?? StreamingSettings.Default; }
        catch { cfg = StreamingSettings.Default; }

        var polish = PreparePolish();
        System.Threading.Channels.ChannelReader<PcmFrame> reader;
        _sessionId++;
        try
        {
            reader = streaming.StartStreaming();
        }
        catch
        {
            SafeCancelRecorder();
            sink.SessionEnded(EndStatus.Failed, 0, VoiceCodes.MicrophoneUnavailable);
            return;
        }

        _stopping = false;
        _recognize = recognize;
        _target = null;
        _polish = polish;
        var run = new StreamRun(_sessionId, new CancellationTokenSource(), recognize, polish as PolishSetup.Ready, null!, StreamingLimits)
        {
            Recorder = streaming,
            Sink = sink,
            Context = e.Context,
        };
        _stream = run;
        SetState(SessionState.Recording);
        sink.SessionStarted();
        _ = Task.Run(() => PumpAsync(run, reader, SegmentSplitter.ForSeconds(cfg.IntervalSeconds)));
        _ = Task.Run(() => TickAsync(run));
    }

    private void ScheduleEngine(StreamRun run)
    {
        CheckDeadlines(run);
        EmitRaws(run);        // 原文は整文の開始より前に送る
        StartPolishes(run);
        StartRecognitions(run);
        EmitPolished(run);
        TryFinishEngine(run);
    }

    /// <summary>原文を連番順に送る。先頭が認識待ちなら、後続が先に完了していても待つ（有界バッファは区間の滞留上限が担う）。</summary>
    private static void EmitRaws(StreamRun run)
    {
        var sink = run.Sink!;
        while (run.Segs.TryGetValue(run.NextSeq, out var s))
        {
            switch (s.State)
            {
                case SegState.Queued or SegState.Recognizing:
                    return;
                case SegState.Silent:
                    sink.RawSegment(s.Seq, RawStatus.Silent, null, false, null);
                    run.Segs.Remove(s.Seq);
                    break;
                case SegState.Dropped:
                    sink.RawSegment(s.Seq, RawStatus.Failed, null, false, s.Code ?? VoiceCodes.RecognitionFailed);
                    run.Segs.Remove(s.Seq);
                    break;
                default:
                    s.RawSent = true;
                    sink.RawSegment(s.Seq, RawStatus.Ok, s.Raw, run.Polish is not null, null);
                    if (run.Polish is null) run.Segs.Remove(s.Seq);
                    break;
            }
            run.SentSeq = run.NextSeq;
            run.NextSeq++;
        }
    }

    /// <summary>整文の結果を、原文を送り終えた区間について送る（区間順は問わない）。失敗は原文のまま。</summary>
    private static void EmitPolished(StreamRun run)
    {
        var sink = run.Sink!;
        foreach (var s in run.Segs.Values.ToArray())
        {
            if (!s.RawSent || s.State != SegState.Ready) continue;
            if (s.RawFallback) sink.PolishedSegment(s.Seq, null, s.PolishCode ?? VoiceCodes.PolishFailed);
            else sink.PolishedSegment(s.Seq, s.Final, null);
            run.Segs.Remove(s.Seq);
        }
    }

    private void TryFinishEngine(StreamRun run)
    {
        if (!run.IntakeEnded || run.Segs.Count > 0 || run.NextSeq <= run.LastSeq) return;
        _stream = null;
        try { run.Cts.Cancel(); } catch (ObjectDisposedException) { }
        run.Sink!.SessionEnded(EndStatus.Completed, run.SentSeq, null);
        SetState(SessionState.Idle);
    }
}
