using System.IO.Pipes;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Whisparrot.Core;
using Whisparrot.Infrastructure.Hane;

namespace Whisparrot.Infrastructure.Voice;

/// <summary>Hane から匿名パイプで一度だけ渡される接続情報（設計 §2.1）。トークンは引数・環境変数に載せない。</summary>
public sealed record VoiceHandoff(string PipeName, string Token, int ParentPid)
{
    public static async Task<VoiceHandoff?> ReadAsync(Stream stream, CancellationToken ct)
    {
        JsonObject o;
        try { o = await HaneWire.ReadAsync(stream, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or HaneWire.FrameException or EndOfStreamException or ObjectDisposedException) { return null; }
        var name = o["pipeName"] is JsonValue n && n.TryGetValue<string>(out var ns) ? ns : null;
        var token = o["token"] is JsonValue t && t.TryGetValue<string>(out var ts) ? ts : null;
        var pid = o["parentPid"] is JsonValue p && p.TryGetValue<int>(out var pi) ? pi : 0;
        return VoiceProtocol.IsPipeName(name) && VoiceProtocol.IsToken(token) && pid > 0 ? new VoiceHandoff(name!, token!, pid) : null;
    }
}

public sealed class VoiceEngineOptions
{
    /// <summary>フレーム 1 件の書き込み期限。超えたら接続を失ったものとして静かに終了する。</summary>
    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>接続から <c>hello.ok</c> を受けるまでの期限。</summary>
    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>接続先パイプのサーバー側 PID。Windows 以外（テスト）では差し替える。</summary>
    public Func<NamedPipeClientStream, uint?> ServerPid { get; init; } = static p => HaneNative.ServerProcessId(p.SafePipeHandle);

    public int OwnPid { get; init; } = Environment.ProcessId;

    /// <summary><c>capture.begin</c> を録音へ渡す直前に呼ぶ。設定をセッション開始時の状態へ固定するために使う（設計 §6）。</summary>
    public Action? BeforeCapture { get; init; }
}

/// <summary>
/// エンジン側の接続（設計 §2）。親（Hane）の専用パイプへつなぎ、サーバー PID を親と照合してから <c>hello</c> を送り、<c>hello.ok</c> の後にだけ録音制御を受ける。
/// エンジン → Hane は区間イベント、Hane → エンジンは <c>capture.begin / stop / cancel</c> だけ。切断・契約違反・書き込み期限切れでは録音を解放して静かに終わる（終了コード 0）。
/// 認証前の失敗は録音を始めず終了コード <see cref="ExitHandshake"/>。
/// </summary>
public sealed class VoiceEngineHost(SessionCoordinator coordinator, VoiceEngineOptions? options = null)
{
    public const int ExitOk = 0;
    public const int ExitHandshake = 2;

    private readonly VoiceEngineOptions _o = options ?? new();
    private readonly Channel<JsonObject> _outbox = Channel.CreateUnbounded<JsonObject>(new UnboundedChannelOptions { SingleReader = true });
    private EventSink? _active; // 読み取りスレッドとコーディネーターのスレッドの両方から見る
    private readonly object _lock = new();

    public async Task<int> RunAsync(VoiceHandoff handoff, CancellationToken ct)
    {
        using var pipe = new NamedPipeClientStream(".", handoff.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.Anonymous);
        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connect.CancelAfter(_o.HelloTimeout);
            await pipe.ConnectAsync(connect.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or OperationCanceledException) { return ExitHandshake; }

        // 接続先が親でなければトークンを送らない。
        if (_o.ServerPid(pipe) != (uint)handoff.ParentPid) return ExitHandshake;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            using (var hello = CancellationTokenSource.CreateLinkedTokenSource(cts.Token))
            {
                hello.CancelAfter(_o.HelloTimeout);
                await HaneWire.WriteAsync(pipe, VoiceProtocol.Hello(handoff.Token, _o.OwnPid), hello.Token).ConfigureAwait(false);
                var reply = await HaneWire.ReadAsync(pipe, hello.Token).ConfigureAwait(false);
                if (VoiceProtocol.ParseCommand(reply)?.Type != "hello.ok") return ExitHandshake;
            }
        }
        catch (Exception ex) when (ex is IOException or HaneWire.FrameException or OperationCanceledException or ObjectDisposedException) { return ExitHandshake; }

        var writer = Task.Run(() => WriteLoopAsync(pipe, cts));
        try
        {
            await ReadLoopAsync(pipe, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or HaneWire.FrameException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            // どの終わり方でも録音を解放する（取消 → 以後のイベントは破棄される）。
            lock (_lock) _active = null;
            coordinator.Cancel();
            _outbox.Writer.TryComplete();
            cts.Cancel();
            try { await writer.ConfigureAwait(false); } catch { }
        }
        return ExitOk;
    }

    private async Task ReadLoopAsync(Stream pipe, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var frame = await HaneWire.ReadAsync(pipe, ct).ConfigureAwait(false);
            var cmd = VoiceProtocol.ParseCommand(frame);
            if (cmd is null) return; // 契約違反: 閉じる
            switch (cmd.Type)
            {
                case "capture.begin":
                    EventSink sink;
                    lock (_lock)
                    {
                        if (_active is not null) return; // 同時に一セッション
                        sink = _active = new EventSink(this, cmd.SessionId!);
                    }
                    try { _o.BeforeCapture?.Invoke(); } catch { /* 設定を読み直せなくても直前の固定値で続ける */ }
                    coordinator.BeginCapture(sink, cmd.Context);
                    break;
                case "capture.stop":
                    if (IsActive(cmd.SessionId!)) coordinator.Stop(); // 終了済み・未知の ID は何もしない
                    break;
                case "capture.cancel":
                    if (IsActive(cmd.SessionId!)) coordinator.Cancel();
                    break;
                default:
                    return; // 認証後の hello.ok など
            }
        }
    }

    private bool IsActive(string sessionId)
    {
        lock (_lock) return _active is { } a && a.SessionId == sessionId;
    }

    private async Task WriteLoopAsync(Stream pipe, CancellationTokenSource cts)
    {
        try
        {
            await foreach (var frame in _outbox.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                timeout.CancelAfter(_o.WriteTimeout);
                await HaneWire.WriteAsync(pipe, frame, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or HaneWire.FrameException or ObjectDisposedException)
        {
            cts.Cancel(); // 書けない: 接続を失ったものとして読み取りを止める
        }
    }

    private void Release(EventSink sink)
    {
        lock (_lock) if (ReferenceEquals(_active, sink)) _active = null;
    }

    /// <summary>1 セッション分のイベント。<c>eventNo</c> は 1 から連続し、<c>session.end</c> の後は何も出さない。コーディネーターのスレッドからだけ呼ばれる。</summary>
    private sealed class EventSink(VoiceEngineHost host, string sessionId) : ISegmentSink
    {
        private int _eventNo;
        private bool _ended;

        public string SessionId { get; } = sessionId;

        public void SessionStarted() => Send(new JsonObject { ["type"] = "session.start" });

        public void RawSegment(int seq, RawStatus status, string? text, bool polishPending, string? code)
        {
            var o = new JsonObject { ["type"] = "segment.raw", ["seq"] = seq };
            switch (status)
            {
                case RawStatus.Ok:
                    o["status"] = "ok";
                    o["text"] = text;
                    o["polish"] = polishPending ? "pending" : "off";
                    break;
                case RawStatus.Silent:
                    o["status"] = "silent";
                    break;
                default:
                    o["status"] = "failed";
                    o["code"] = code;
                    break;
            }
            Send(o);
        }

        public void PolishedSegment(int seq, string? text, string? code)
        {
            var o = new JsonObject { ["type"] = "segment.polished", ["seq"] = seq };
            if (text is not null) { o["status"] = "ok"; o["text"] = text; }
            else { o["status"] = "failed"; o["code"] = code; }
            Send(o);
        }

        public void SessionEnded(EndStatus status, int lastSeq, string? code)
        {
            var o = new JsonObject { ["type"] = "session.end", ["status"] = VoiceProtocol.EndStatusName(status), ["lastSeq"] = lastSeq };
            if (code is not null) o["code"] = code;
            Send(o);
            _ended = true;
            host.Release(this);
        }

        private void Send(JsonObject o)
        {
            if (_ended) return;
            o["sessionId"] = SessionId;
            o["eventNo"] = ++_eventNo;
            host._outbox.Writer.TryWrite(o);
        }
    }
}
