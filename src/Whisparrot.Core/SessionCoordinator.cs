using System.Threading.Channels;

namespace Whisparrot.Core;

public enum SessionState
{
    Idle,
    Recording,
    Recognizing,
    Inserting,

    /// <summary>認識済みだが未投入の一件を保持している（トレイから再試行・破棄）。</summary>
    Failed,
}

/// <summary>UI が購読する状態のスナップショット。</summary>
public sealed record SessionSnapshot(SessionState State, long SessionId, bool HasPendingText);

public enum NoticeKind { Info, Error }

/// <summary>控えめな通知（モーダルにしない）。UI はピル／トレイで短く表示する。</summary>
public sealed record SessionNotice(NoticeKind Kind, string Message);

/// <summary>
/// 設計 §3.2 / §3.3 の状態機械。イベントを <see cref="Channel{T}"/> で直列処理する。
/// 長い処理（停止・認識・投入）はタスクとして走らせ、完了はセッション ID 付きのイベントとして戻す。
/// 取消済みセッションの遅れた完了は ID の不一致で捨てる。
/// </summary>
public sealed class SessionCoordinator : IDisposable
{
    private abstract record Evt;
    private sealed record ToggleEvt : Evt;
    private sealed record StopEvt : Evt;
    private sealed record CancelEvt : Evt;
    private sealed record RetryEvt : Evt;
    private sealed record RecorderFailedEvt(RecorderError Error) : Evt;
    private sealed record StoppedEvt(long Id, PcmAudio? Pcm, Exception? Error) : Evt;
    private sealed record RecognizedEvt(long Id, RecognitionResult? Result, Exception? Error) : Evt;
    private sealed record InjectedEvt(long Id, InjectResult Result) : Evt;
    private sealed record BarrierEvt(TaskCompletionSource Tcs) : Evt;

    private readonly IRecorder _recorder;
    private readonly IRecognizer _recognizer;
    private readonly ITextInjector _injector;
    private readonly Channel<Evt> _events = Channel.CreateUnbounded<Evt>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _loop;
    private readonly CancellationTokenSource _lifetime = new();

    private SessionState _state = SessionState.Idle;
    private long _sessionId;
    private CancellationTokenSource? _sessionCts;
    private bool _stopping;          // Recording 中に停止処理を開始済み（Stopped 待ち）
    private string? _pendingText;    // 未投入の一件（Failed 状態でだけ保持）

    public SessionCoordinator(IRecorder recorder, IRecognizer recognizer, ITextInjector injector)
    {
        _recorder = recorder;
        _recognizer = recognizer;
        _injector = injector;
        _recorder.Failed += OnRecorderFailed;
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>状態変化。ループスレッドから呼ばれる。UI 側でディスパッチすること。</summary>
    public event Action<SessionSnapshot>? StateChanged;

    public event Action<SessionNotice>? Noticed;

    /// <summary>認識が成功したときの実際の実行方式（設定画面の表示用）。</summary>
    public event Action<string>? BackendReported;

    public SessionSnapshot Snapshot => new(_state, _sessionId, _pendingText is not null);

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
            }
            catch (Exception ex)
            {
                // 状態機械自身の想定外。資源を解放して待機へ戻す。
                SafeCancelRecorder();
                _sessionCts?.Cancel();
                _pendingText = null;
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
            case InjectedEvt i: OnInjected(i); break;
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
        var problem = _recognizer.CheckReady();
        if (problem is not null)
        {
            Notify(NoticeKind.Error, problem);
            return;
        }
        _sessionId++;
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
        SetState(SessionState.Recording);
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
        SetState(SessionState.Recognizing);
        _ = Task.Run(async () =>
        {
            try
            {
                Post(new RecognizedEvt(id, await _recognizer.RecognizeAsync(new RecognitionRequest(pcm), cts.Token), null));
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
        StartInject(text);
    }

    private void StartInject(string text)
    {
        // 投入確定の境界: ここから先はキャンセルしない（部分投入の取り消し・自動方式変更をしない）。
        _pendingText = text;
        var id = _sessionId;
        SetState(SessionState.Inserting);
        _ = Task.Run(async () =>
        {
            try
            {
                Post(new InjectedEvt(id, await _injector.InjectAsync(text, CancellationToken.None)));
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
        StartInject(_pendingText);
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
            case SessionState.Failed:
                _pendingText = null;
                SetState(SessionState.Idle);
                break;
            // Inserting: 投入確定後は取消不可。
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
        SafeCancelRecorder();
        _lifetime.Dispose();
    }
}
