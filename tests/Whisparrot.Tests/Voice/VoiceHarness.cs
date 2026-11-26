using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using Whisparrot.Core;
using Whisparrot.Core.Settings;
using Whisparrot.Infrastructure.Hane;
using Whisparrot.Infrastructure.Voice;

namespace Whisparrot.Tests.Voice;

/// <summary>
/// voice/1 の検証用ホスト（Hane の代わり）。接続ランダム値を含む専用名前付きパイプを作り、エンジン（同一プロセス内の <see cref="VoiceEngineHost"/>）を接続させ、
/// <c>hello</c> を検証して <c>hello.ok</c> を返し、<c>capture.*</c> を送り、返るイベントを記録する。固定 PCM・偽認識・偽整文は <see cref="EngineRig"/> が組む。
/// P3 で実プロセスのエンジンを同じ契約検証にかけるときも、<see cref="VoiceContract"/> を再利用できる。
/// </summary>
internal sealed class VoiceTestHost : IAsyncDisposable
{
    public const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private readonly NamedPipeServerStream _pipe;
    private readonly ConcurrentQueue<JsonObject> _events = new();
    private readonly CancellationTokenSource _cts = new();
    private Task _reader = Task.CompletedTask;

    public EngineRig Rig { get; }
    public Task<int>? Engine { get; private set; }
    public JsonObject? Hello { get; private set; }
    // macOS の名前付きパイプは Unix ドメインソケット（パス 104 文字まで）なので短くする。Windows の実パイプ名は接続ランダム値を含む長い名前。
    public string PipeName { get; } = "wpv" + Convert.ToHexString(Guid.NewGuid().ToByteArray())[..12].ToLowerInvariant();

    private VoiceTestHost(EngineRig rig)
    {
        Rig = rig;
        _pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    public IReadOnlyList<JsonObject> Events => _events.ToArray();

    /// <summary>エンジンを起動して接続・認証を済ませる。<paramref name="serverPid"/> で接続先 PID の照合を差し替えられる（既定は自プロセス）。</summary>
    public static async Task<VoiceTestHost> StartAsync(EngineRig rig, uint? serverPid = null, int? parentPid = null, bool acceptHello = true)
    {
        var host = new VoiceTestHost(rig);
        var self = (uint)Environment.ProcessId;
        var options = new VoiceEngineOptions
        {
            ServerPid = _ => serverPid ?? self,
            HelloTimeout = TimeSpan.FromSeconds(3),
            WriteTimeout = TimeSpan.FromSeconds(3),
        };
        var handoff = new VoiceHandoff(host.PipeName, Token, parentPid ?? (int)self);
        host.Engine = Task.Run(() => new VoiceEngineHost(rig.Coordinator, options).RunAsync(handoff, CancellationToken.None));

        await host._pipe.WaitForConnectionAsync(host._cts.Token);
        if (serverPid is not null && serverPid != self) return host; // 接続先の照合に失敗したエンジンは hello を送らない
        host.Hello = await HaneWire.ReadAsync(host._pipe, host._cts.Token);
        if (acceptHello && host.Hello["token"]?.GetValue<string>() == Token && host.Hello["protocol"]?.GetValue<string>() == "voice/1")
        {
            await HaneWire.WriteAsync(host._pipe, new JsonObject { ["type"] = "hello.ok", ["protocol"] = "voice/1" }, host._cts.Token);
            host._reader = Task.Run(host.ReadLoop);
        }
        return host;
    }

    private async Task ReadLoop()
    {
        try
        {
            while (true) _events.Enqueue(await HaneWire.ReadAsync(_pipe, _cts.Token));
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or EndOfStreamException or ObjectDisposedException or HaneWire.FrameException) { }
    }

    public static string NewSessionId() => Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant();

    public Task SendAsync(JsonObject frame) => HaneWire.WriteAsync(_pipe, frame, _cts.Token);

    /// <summary><c>capture.begin</c> を送り、既定ではエンジンの最初のイベント（<c>session.start</c>、開始失敗なら <c>session.end</c>）を待つ。偽録音のチャンネルは開始時に作られるので、PCM を流すのはその後。</summary>
    public async Task<string> BeginAsync(JsonObject? context = null, string? sessionId = null, bool waitFirstEvent = true)
    {
        var id = sessionId ?? NewSessionId();
        var f = new JsonObject { ["type"] = "capture.begin", ["sessionId"] = id, ["options"] = new JsonObject() };
        if (context is not null) f["context"] = context;
        await SendAsync(f);
        if (waitFirstEvent) await UntilAsync(ev => ev.Any(e => (string?)e["sessionId"] == id), "first event of " + id[..4]);
        return id;
    }

    public Task StopAsync(string id) => SendAsync(new JsonObject { ["type"] = "capture.stop", ["sessionId"] = id });

    public Task CancelAsync(string id) => SendAsync(new JsonObject { ["type"] = "capture.cancel", ["sessionId"] = id });

    /// <summary>接続を閉じる（Hane の終了・接続喪失）。</summary>
    public void Disconnect() => _pipe.Dispose();

    public IReadOnlyList<JsonObject> ForSession(string id) => Events.Where(e => (string?)e["sessionId"] == id).ToArray();

    public Task UntilAsync(Func<IReadOnlyList<JsonObject>, bool> condition, string what, int timeoutMs = 5000) =>
        Wait.UntilAsync(() => condition(Events), what + " (events: " + Describe(Events) + ")", timeoutMs);

    public Task UntilEndAsync(string id, int timeoutMs = 5000) =>
        UntilAsync(ev => ev.Any(e => (string?)e["sessionId"] == id && (string?)e["type"] == "session.end"), $"session.end of {id[..4]}", timeoutMs);

    /// <summary>1 行 1 イベントの要約（type[:seq][:status/polish][:code]）。本文は含めない。</summary>
    public static string Describe(IEnumerable<JsonObject> events) =>
        string.Join(" | ", events.Select(e => string.Join(":", new[] { e["type"]?.ToString(), e["seq"]?.ToString(), e["status"]?.ToString(), e["polish"]?.ToString(), e["code"]?.ToString() }.Where(x => x is not null))));

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _pipe.Dispose();
        if (Engine is not null) await Task.WhenAny(Engine, Task.Delay(3000));
        try { await _reader; } catch { }
        Rig.Dispose();
        _cts.Dispose();
    }
}

/// <summary>固定 PCM を流す偽録音・偽認識・偽整文の組。認識結果は PCM の先頭サンプルの値で区間を識別する（<c>文N</c>）。</summary>
internal sealed class EngineRig : IDisposable
{
    public readonly FakeStreamingRecorder Recorder = new();
    public readonly FakeInjector Injector = new();
    public SessionCoordinator Coordinator { get; }
    public readonly ConcurrentQueue<PolishInput> PolishInputs = new();

    public EngineRig(Func<RecognitionRequest, CancellationToken, Task<RecognitionResult>>? recognize = null,
        Func<string, CancellationToken, Task<PolishResult>>? polish = null, TimeSpan? polishTimeout = null, StreamingLimits? limits = null,
        RecognitionEngine engine = RecognitionEngine.Cloud, int intervalSeconds = 4)
    {
        recognize ??= (r, _) => Task.FromResult(new RecognitionResult($"文{(int)r.Pcm.Samples[0]}", "cloud"));
        ITextPolisher? polisher = null;
        if (polish is not null)
        {
            var timeout = polishTimeout ?? TimeSpan.FromSeconds(15);
            polisher = new RecordingPolisher(polish, timeout, PolishInputs);
        }
        // ストリーミング設定の Enabled は見ない（エンジンモードは区間の秒数だけを使う）。
        Coordinator = new SessionCoordinator(Recorder, new FakeRecognitionSource(recognize, engine), Injector, polisher, null, () => new StreamingSettings(false, intervalSeconds));
        if (limits is not null) Coordinator.StreamingLimits = limits;
    }

    /// <summary>全サンプルが <paramref name="value"/> の区間を 1 つ分流す（<see cref="StreamingSettings.IntervalSeconds"/> 秒）。</summary>
    public void Segment(float value, double seconds = 4) => Recorder.PushSeconds(value, seconds);

    public void Dispose() => Coordinator.Dispose();

    private sealed class RecordingPolisher(Func<string, CancellationToken, Task<PolishResult>> handler, TimeSpan timeout, ConcurrentQueue<PolishInput> inputs) : ITextPolisher
    {
        public PolishSetup Prepare() => new PolishSetup.Ready((s, ct) => { inputs.Enqueue(new PolishInput(s)); return handler(s, ct); }, timeout)
        {
            PolishWithContextAsync = (input, ct) => { inputs.Enqueue(input); return handler(input.Utterance, ct); },
        };
    }
}

/// <summary>
/// voice/1 の受信側が検証する契約（設計 §2.2）。違反があれば理由の一覧を返す。<c>session.start</c> を最初に置く、eventNo が 1 から連続、
/// sessionId 一致、原文 seq が 1 から連続、整文は原文 ok/pending の後に一件だけ、<c>session.end</c> は一度だけで最後、<c>lastSeq</c> 一致、
/// 完了時に未終端の整文がない、を見る。
/// </summary>
internal static class VoiceContract
{
    public static List<string> Violations(IReadOnlyList<JsonObject> events, string sessionId)
    {
        var v = new List<string>();
        var no = 0;
        var lastRaw = 0;
        var pending = new HashSet<int>();
        var polishedSeen = new HashSet<int>();
        var started = false;
        string? endStatus = null;
        var endLast = -1;
        foreach (var e in events)
        {
            var type = (string?)e["type"];
            if (endStatus is not null) v.Add($"event after session.end: {type}");
            if ((string?)e["sessionId"] != sessionId) v.Add($"sessionId mismatch at {type}");
            if ((int?)e["eventNo"] != ++no) v.Add($"eventNo {(int?)e["eventNo"]} at position {no}");
            switch (type)
            {
                case "session.start":
                    if (no != 1) v.Add("session.start is not first");
                    started = true;
                    break;
                case "segment.raw":
                {
                    if (!started) v.Add("segment.raw before session.start");
                    var seq = (int?)e["seq"] ?? -1;
                    if (seq != lastRaw + 1) v.Add($"raw seq {seq} after {lastRaw}");
                    lastRaw = seq;
                    var status = (string?)e["status"];
                    var text = (string?)e["text"];
                    if (status == "ok")
                    {
                        if (string.IsNullOrEmpty(text)) v.Add($"raw {seq} ok without text");
                        var polish = (string?)e["polish"];
                        if (polish is not ("pending" or "off")) v.Add($"raw {seq} polish={polish}");
                        if (polish == "pending") pending.Add(seq);
                        if (e["code"] is not null) v.Add($"raw {seq} ok with code");
                    }
                    else if (status is "silent" or "failed")
                    {
                        if (text is not null) v.Add($"raw {seq} {status} with text");
                        if (e["polish"] is not null) v.Add($"raw {seq} {status} with polish");
                        if ((status == "failed") != (e["code"] is not null)) v.Add($"raw {seq} {status} code presence");
                    }
                    else v.Add($"raw {seq} status={status}");
                    break;
                }
                case "segment.polished":
                {
                    var seq = (int?)e["seq"] ?? -1;
                    if (!pending.Contains(seq)) v.Add($"polished {seq} without pending raw");
                    if (!polishedSeen.Add(seq)) v.Add($"polished {seq} twice");
                    var status = (string?)e["status"];
                    if (status == "ok" && string.IsNullOrEmpty((string?)e["text"])) v.Add($"polished {seq} ok without text");
                    if (status == "failed" && (e["text"] is not null || e["code"] is null)) v.Add($"polished {seq} failed shape");
                    if (status is not ("ok" or "failed")) v.Add($"polished {seq} status={status}");
                    break;
                }
                case "session.end":
                    endStatus = (string?)e["status"];
                    endLast = (int?)e["lastSeq"] ?? -1;
                    if (endLast != lastRaw) v.Add($"lastSeq {endLast} != last raw {lastRaw}");
                    if (endStatus == "completed" && pending.Except(polishedSeen).Any()) v.Add("completed with unterminated polish");
                    if (endStatus is "failed" && e["code"] is null) v.Add("failed end without code");
                    if (endStatus is not ("completed" or "cancelled" or "failed")) v.Add($"end status={endStatus}");
                    break;
                default:
                    v.Add($"unknown type {type}");
                    break;
            }
        }
        if (!started && !(events.Count == 1 && endStatus is not null)) v.Add("no session.start");
        if (endStatus is null) v.Add("no session.end");
        return v;
    }
}
