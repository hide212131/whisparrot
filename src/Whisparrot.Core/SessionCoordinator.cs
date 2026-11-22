using System.Threading.Channels;
using Whisparrot.Core.Hane;
using Whisparrot.Core.Settings;

namespace Whisparrot.Core;

public enum SessionState
{
    Idle,
    Recording,
    Recognizing,

    /// <summary>認識済みの原文を保持して整文中（設計 §3.3）。取消しても原文は投入しない。</summary>
    Polishing,
    Inserting,

    /// <summary>Hane 連携で原文を先に挿入中（設計 §4.5）。編集要求の発行済みで取り消せない。挿入が確定すると Idle に戻り、整文は別ジョブとして続く。</summary>
    InsertingRaw,

    /// <summary>認識済みだが未投入の一件を保持している（トレイから再試行・破棄）。</summary>
    Failed,
}

/// <summary>UI が購読する状態のスナップショット。<paramref name="Engine"/> は進行中のセッションが録音開始時に固定した認識先（Idle では null）。</summary>
public sealed record SessionSnapshot(SessionState State, long SessionId, bool HasPendingText, RecognitionEngine? Engine = null);

public enum NoticeKind { Info, Warning, Error }

/// <summary>控えめな通知（モーダルにしない）。UI はピル／トレイで短く表示する。</summary>
public sealed record SessionNotice(NoticeKind Kind, string Message);

/// <summary>
/// 設計 §3.2 / §3.3 の状態機械。イベントを <see cref="Channel{T}"/> で直列処理する。
/// 長い処理（停止・認識・投入）はタスクとして走らせ、完了はセッション ID 付きのイベントとして戻す。
/// 取消済みセッションの遅れた完了は ID の不一致で捨てる。
/// </summary>
public sealed partial class SessionCoordinator : IDisposable
{
    private abstract record Evt;
    private sealed record ToggleEvt : Evt;
    private sealed record StopEvt : Evt;
    private sealed record CancelEvt : Evt;
    private sealed record RetryEvt : Evt;
    private sealed record RecorderFailedEvt(RecorderError Error) : Evt;
    private sealed record StoppedEvt(long Id, PcmAudio? Pcm, Exception? Error) : Evt;
    private sealed record RecognizedEvt(long Id, RecognitionResult? Result, Exception? Error) : Evt;
    private sealed record PolishedEvt(long Id, PolishResult Result) : Evt;
    private sealed record InjectedEvt(long Id, InjectResult Result) : Evt;
    private sealed record BarrierEvt(TaskCompletionSource Tcs) : Evt;

    private readonly IRecorder _recorder;
    private readonly IRecognitionSource _recognition;
    private readonly ITextInjector _injector;
    private readonly ITextPolisher? _polisher;
    private readonly Channel<Evt> _events = Channel.CreateUnbounded<Evt>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _loop;

    private SessionState _state = SessionState.Idle;
    private long _sessionId;
    private CancellationTokenSource? _sessionCts;
    private bool _stopping;          // Recording 中に停止処理を開始済み（Stopped 待ち）
    private string? _rawText;        // 整文中の原文（整文が終わる／取消されるまで）
    private string? _pendingText;    // 未投入の一件（Failed 状態でだけ保持）
    private PolishSetup _polish = new PolishSetup.Off(); // 録音開始時に固定する整文の構え
    private RecognitionSetup.Ready? _recognize;          // 録音開始時に固定する認識の構え（クラウドの宛先・キーを含む）
    private IInjectTarget? _target;                      // 録音開始時に記録した投入先（最初の投入だけが使う。再試行は投入時点の前景）

    public SessionCoordinator(IRecorder recorder, IRecognizer recognizer, ITextInjector injector, ITextPolisher? polisher = null, IHaneLink? hane = null)
        : this(recorder, new FixedRecognitionSource(recognizer), injector, polisher, hane)
    {
    }

    public SessionCoordinator(IRecorder recorder, IRecognitionSource recognition, ITextInjector injector, ITextPolisher? polisher = null, IHaneLink? hane = null)
    {
        _haneLink = hane;
        _recorder = recorder;
        _recognition = recognition;
        _injector = injector;
        _polisher = polisher;
        _recorder.Failed += OnRecorderFailed;
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>状態変化。ループスレッドから呼ばれる。UI 側でディスパッチすること。</summary>
    public event Action<SessionSnapshot>? StateChanged;

    public event Action<SessionNotice>? Noticed;

    /// <summary>認識が成功したときの実際の実行方式（設定画面の表示用）。</summary>
    public event Action<string>? BackendReported;

    public SessionSnapshot Snapshot => new(_state, _sessionId, _pendingText is not null, _state == SessionState.Idle ? null : _recognize?.Engine);

    /// <summary>ホットキーまたはトレイの開始/停止。Idle なら録音開始、Recording なら停止。それ以外は無視。</summary>
    public void Toggle() => Post(new ToggleEvt());

    public void Stop() => Post(new StopEvt());

    /// <summary>ピルの取消。録音・認識中は破棄、未投入の一件があれば破棄する。投入確定後は無効。</summary>
    public void Cancel() => Post(new CancelEvt());

    /// <summary>未投入の一件を同じ方式で再投入する。</summary>
    public void RetryInsert() => Post(new RetryEvt());

    /// <summary>テスト用: ここまでに投入したイベントがすべて処理されるまで待つ。</summary>
    public Task FlushAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(new BarrierEvt(tcs));
        return tcs.Task;
    }

    private void Post(Evt e) => _events.Writer.TryWrite(e);

    private void OnRecorderFailed(RecorderError error) => Post(new RecorderFailedEvt(error));

    private async Task LoopAsync()
    {
        await foreach (var e in _events.Reader.ReadAllAsync())
        {
            try
            {
                Handle(e);
                PumpJobs();
            }
            catch (Exception ex)
            {
                // 状態機械自身の想定外。資源を解放して待機へ戻す。
                SafeCancelRecorder();
                _sessionCts?.Cancel();
                _pendingText = null;
                _rawText = null;
                ClearHaneSession();
                Notify(NoticeKind.Error, $"内部エラー: {ex.GetType().Name}");
                SetState(SessionState.Idle);
            }
        }
    }

    private void Handle(Evt e)
    {
        switch (e)
        {
            case BarrierEvt b: b.Tcs.TrySetResult(); break;
            case ToggleEvt: OnToggle(); break;
            case StopEvt: if (_state == SessionState.Recording) BeginStop(); break;
            case CancelEvt: OnCancel(); break;
            case RetryEvt: OnRetry(); break;
            case RecorderFailedEvt f: OnRecorderFailedEvt(f); break;
            case StoppedEvt s: OnStopped(s); break;
            case RecognizedEvt r: OnRecognized(r); break;
            case PolishedEvt p: OnPolished(p); break;
            case InjectedEvt i: OnInjected(i); break;
            case HaneContextEvt h: OnHaneContext(h); break;
            case HaneInsertedEvt h: OnHaneInserted(h); break;
            case JobPolishedEvt j: OnJobPolished(j); break;
            case JobReplacedEvt j: OnJobReplaced(j); break;
            case JobExpiredEvt j: OnJobExpired(j); break;
            case DiscardHeldEvt: ReleaseHeld(); break;
            case HeldExpiredEvt h: if (h.Seq == _heldSeq) ReleaseHeld(); break;
        }
    }

    private void OnToggle()
    {
        switch (_state)
        {
            case SessionState.Idle: BeginRecording(); break;
            case SessionState.Recording: BeginStop(); break;
            // 認識/投入/失敗中の再押下は新録音を始めない（設計 §3.3）。
        }
    }

    private void BeginRecording()
    {
        var setup = PrepareRecognition();
        if (setup is RecognitionSetup.Unconfigured u)
        {
            Notify(NoticeKind.Error, u.Reason);
            return;
        }
        _sessionId++;
        var target = CaptureTarget();
        try
        {
            _recorder.Start();
        }
        catch (Exception ex)
        {
            SafeCancelRecorder();
            Notify(NoticeKind.Error, ex is RecorderException ? ex.Message : "マイクを開始できませんでした");
            return;
        }
        _stopping = false;
        _recognize = (RecognitionSetup.Ready)setup;
        _target = target;
        _polish = PreparePolish();
        SetState(SessionState.Recording);
        StartHaneContext(target); // マイク開始後に非同期で取得する。録音開始は待たせない
    }

    private IInjectTarget? CaptureTarget()
    {
        try { return _injector.CaptureTarget(); }
        catch { return null; } // 記録できなければ従来の「投入時点の前景」へ倒す
    }

    private RecognitionSetup PrepareRecognition()
    {
        try { return _recognition.Prepare(); }
        catch { return new RecognitionSetup.Unconfigured("認識の設定を読み込めません"); }
    }

    private PolishSetup PreparePolish()
    {
        if (_polisher is null) return new PolishSetup.Off();
        try { return _polisher.Prepare(); }
        catch { return new PolishSetup.Unconfigured("整文の設定を読み込めません"); }
    }

    private void BeginStop()
    {
        if (_stopping) return;
        _stopping = true;
        var id = _sessionId;
        _ = Task.Run(async () =>
        {
            try
            {
                Post(new StoppedEvt(id, await _recorder.StopAsync(), null));
            }
            catch (Exception ex)
            {
                Post(new StoppedEvt(id, null, ex));
            }
        });
    }

    private void OnStopped(StoppedEvt s)
    {
        if (s.Id != _sessionId || _state != SessionState.Recording) return; // 取消済み
        _stopping = false;
        if (s.Pcm is null)
        {
            SafeCancelRecorder();
            Notify(NoticeKind.Error, "録音を完了できませんでした");
            SetState(SessionState.Idle);
            return;
        }
        if (s.Pcm.Samples.Length == 0)
        {
            Notify(NoticeKind.Info, "音声がありませんでした");
            SetState(SessionState.Idle);
            return;
        }
        var cts = _sessionCts = new CancellationTokenSource();
        var id = _sessionId;
        var pcm = s.Pcm;
        var recognize = _recognize!;
        var token = cts.Token;
        SetState(SessionState.Recognizing);
        _ = Task.Run(async () =>
        {
            try
            {
                var request = new RecognitionRequest(pcm);
                if (recognize.Deadline is not { } deadline)
                {
                    Post(new RecognizedEvt(id, await recognize.RecognizeAsync(request, token), null));
                    return;
                }

                // 認識側が期限を守らなくても、期限でセッションを終える。結果を採るのは先に着いた一回だけで、
                // 期限後の遅れた成功は投稿されない（取消を無視した認識が後で完了しても何も起きない）。
                var work = Task.Run(() => recognize.RecognizeAsync(request, token), CancellationToken.None);
                _ = work.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                using var timer = CancellationTokenSource.CreateLinkedTokenSource(token);
                var winner = await Task.WhenAny(work, Task.Delay(deadline, timer.Token)).ConfigureAwait(false);
                if (winner == work)
                {
                    timer.Cancel();
                    Post(new RecognizedEvt(id, await work.ConfigureAwait(false), null));
                    return;
                }
                if (token.IsCancellationRequested) return; // 利用者の取消が先
                // 先に取消して要求・応答を止め、その後に期限切れを知らせる（Post 後はセッションの CTS が破棄され得る）。
                try { cts.Cancel(); } catch (ObjectDisposedException) { /* 先に終わったセッション */ }
                Post(new RecognizedEvt(id, null, new RecognitionException("文字起こしできませんでした（時間切れ）")));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // 利用者の取消。状態はすでに変わっており、結果は使われない。
            }
            catch (Exception ex)
            {
                Post(new RecognizedEvt(id, null, ex));
            }
        });
    }

    private void OnRecognized(RecognizedEvt r)
    {
        if (r.Id != _sessionId || _state != SessionState.Recognizing) return; // 取消後の遅れた完了
        DisposeSessionCts();
        if (r.Error is not null)
        {
            if (r.Error is not OperationCanceledException)
            {
                Notify(NoticeKind.Error, r.Error is RecognitionException ? r.Error.Message : "認識に失敗しました");
            }
            SetState(SessionState.Idle);
            return;
        }
        var text = r.Result!.Text.Trim();
        BackendReported?.Invoke(r.Result.Backend);
        if (text.Length == 0)
        {
            Notify(NoticeKind.Info, "音声を認識できませんでした");
            SetState(SessionState.Idle);
            return;
        }
        if (UseHane() && _hane!.InsertRawFirst)
        {
            StartHaneInsert(text, rawFirst: true);
            return;
        }
        switch (_polish)
        {
            case PolishSetup.Ready ready:
                StartPolish(text, ready);
                break;
            case PolishSetup.Unconfigured u:
                // 整文を試みず原文を投入する。状態は控えめに知らせる。
                Notify(NoticeKind.Warning, $"整文の設定が未完了のため、そのまま入力します（{u.Reason}）");
                StartDeliver(text);
                break;
            default:
                StartDeliver(text);
                break;
        }
    }

    private void StartPolish(string raw, PolishSetup.Ready ready)
    {
        var cts = _sessionCts = new CancellationTokenSource();
        var id = _sessionId;
        _rawText = raw;
        SetState(SessionState.Polishing);
        _ = Task.Run(async () =>
        {
            try
            {
                // 整文側の実装が期限を守らなくても、期限で必ず原文へ戻る（取消された場合はイベントを捨てられる）。
                var work = Task.Run(() => ready.PolishAsync(raw, cts.Token), CancellationToken.None);
                _ = work.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                var winner = await Task.WhenAny(work, Task.Delay(ready.Timeout, cts.Token)).ConfigureAwait(false);
                Post(new PolishedEvt(id, winner == work
                    ? await work.ConfigureAwait(false)
                    : PolishResult.Fallback(PolishFailure.Timeout)));
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // 利用者の取消。状態はすでに変わっており、結果は使われない。
            }
            catch
            {
                Post(new PolishedEvt(id, PolishResult.Fallback(PolishFailure.Error)));
            }
        });
    }

    private void OnPolished(PolishedEvt p)
    {
        if (p.Id != _sessionId || _state != SessionState.Polishing) return; // 取消後の遅れた完了
        DisposeSessionCts();
        var raw = _rawText!;
        _rawText = null;
        var r = p.Result;
        if (r.IsSuccess && !string.IsNullOrWhiteSpace(r.Text))
        {
            StartDeliver(r.Text.Trim());
            return;
        }
        var why = r.IsSuccess ? "空の応答" : r.Failure switch
        {
            PolishFailure.Timeout => "時間切れ",
            PolishFailure.Network => "接続できません",
            PolishFailure.Auth => "認証エラー",
            PolishFailure.Http => r.Detail ?? "サーバーエラー",
            PolishFailure.Empty => "空の応答",
            PolishFailure.TooLarge => "応答が大きすぎます",
            PolishFailure.Incomplete => "応答が未完了",
            _ => "応答を解釈できません",
        };
        Notify(NoticeKind.Warning, $"整文できなかったため、そのまま入力しました（{why}）");
        StartDeliver(raw);
    }

    private void StartInject(string text, bool retry = false)
    {
        // 投入確定の境界: ここから先はキャンセルしない（部分投入の取り消し・自動方式変更をしない）。
        _pendingText = text;
        var id = _sessionId;
        // 記録した投入先は最初の投入だけが使う。再試行は、その時点の前面ウィンドウを対象とする（従来動作）。
        var target = retry ? null : _target;
        _target = null;
        SetState(SessionState.Inserting);
        _ = Task.Run(async () =>
        {
            try
            {
                Post(new InjectedEvt(id, await _injector.InjectAsync(text, target, CancellationToken.None)));
            }
            catch (Exception ex)
            {
                Post(new InjectedEvt(id, new InjectResult(InjectOutcome.Failed, ex.Message)));
            }
        });
    }

    private void OnInjected(InjectedEvt i)
    {
        if (i.Id != _sessionId || _state != SessionState.Inserting) return;
        if (i.Result.Outcome == InjectOutcome.Success)
        {
            _pendingText = null;
            SetState(SessionState.Idle);
            return;
        }
        // 未投入の一件だけをメモリに保持し、トレイから再試行・破棄できる。
        Notify(NoticeKind.Error, i.Result.Message ?? "入力できませんでした");
        SetState(SessionState.Failed);
    }

    private void OnRetry()
    {
        if (_state != SessionState.Failed || _pendingText is null) return;
        StartInject(_pendingText, retry: true);
    }

    private void OnCancel()
    {
        switch (_state)
        {
            case SessionState.Recording:
                SafeCancelRecorder();
                _stopping = false;
                _sessionId++; // 停止タスクの遅れた完了を捨てる
                SetState(SessionState.Idle);
                break;
            case SessionState.Recognizing:
                _sessionCts?.Cancel();
                DisposeSessionCts();
                _sessionId++;
                SetState(SessionState.Idle);
                break;
            case SessionState.Polishing:
                // 取消は投入しない（設計 §7.1: キャンセル時に原文を勝手に貼り付けない。M1 の取消と同じ）。
                _sessionCts?.Cancel();
                DisposeSessionCts();
                _rawText = null;
                _sessionId++;
                SetState(SessionState.Idle);
                break;
            case SessionState.Failed:
                _pendingText = null;
                SetState(SessionState.Idle);
                break;
            case SessionState.Idle:
                CancelPolishJobs(); // 原文確定後の整文だけを取り消す
                break;
            // Inserting / InsertingRaw: 投入確定後は取消不可。
        }
    }

    private void OnRecorderFailedEvt(RecorderFailedEvt f)
    {
        if (_state != SessionState.Recording) return;
        SafeCancelRecorder();
        _stopping = false;
        _sessionId++;
        Notify(NoticeKind.Error, f.Error.Message);
        SetState(SessionState.Idle);
    }

    private void SafeCancelRecorder()
    {
        try { _recorder.Cancel(); } catch { /* 解放を試みた上で握りつぶす */ }
    }

    private void DisposeSessionCts()
    {
        _sessionCts?.Dispose();
        _sessionCts = null;
    }

    private void SetState(SessionState state)
    {
        _state = state;
        if (state is SessionState.Idle or SessionState.Failed) ClearHaneSession(); // 再試行は従来入力（利用者の明示操作）
        StateChanged?.Invoke(Snapshot);
    }

    private void Notify(NoticeKind kind, string message) => Noticed?.Invoke(new SessionNotice(kind, message));

    public void Dispose()
    {
        _recorder.Failed -= OnRecorderFailed;
        _events.Writer.TryComplete();
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _sessionCts?.Cancel();
        _sessionCts?.Dispose();
        ShutdownHane();
        SafeCancelRecorder();
    }
}
