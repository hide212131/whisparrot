using System.Text.Json.Nodes;
using Whisparrot.Core;
using Whisparrot.Core.Settings;
using Whisparrot.Infrastructure.Hane;
using Whisparrot.Infrastructure.Voice;

namespace Whisparrot.Tests.Voice;

/// <summary>
/// P2 エンジンモード。検証用ホスト（Hane の代わり）がパイプを作ってエンジンを接続させ、固定 PCM・偽認識・偽整文を通して、
/// 返るイベント列が契約どおり（順序・eventNo の連続・sessionId・終了状態）になることを確かめる。
/// </summary>
public sealed class VoiceEngineTests
{
    private static StreamingLimits Fast(Func<StreamingLimits, StreamingLimits>? tweak = null)
    {
        var l = new StreamingLimits { Tick = TimeSpan.FromMilliseconds(10) };
        return tweak?.Invoke(l) ?? l;
    }

    private static Task<PolishResult> Ok(string s, CancellationToken _) => Task.FromResult(PolishResult.Success("整:" + s));

    private static string Summary(VoiceTestHost h, string id) => VoiceTestHost.Describe(h.ForSession(id));

    private static void AssertContract(VoiceTestHost h, string id) => Assert.Empty(VoiceContract.Violations(h.ForSession(id), id));

    // ── 正常系 ──

    [Fact]
    public async Task Normal_session_with_polish_yields_a_contract_conforming_stream()
    {
        await using var h = await VoiceTestHost.StartAsync(new EngineRig(polish: Ok, limits: Fast()));
        var id = await h.BeginAsync();
        await h.UntilAsync(e => e.Count == 1, "session.start");
        h.Rig.Segment(1);
        h.Rig.Segment(2);
        h.Rig.Segment(3, 1);
        await h.UntilAsync(e => e.Count(x => (string?)x["type"] == "segment.raw") == 2, "two raws during recording");
        await h.StopAsync(id);
        await h.UntilEndAsync(id);

        AssertContract(h, id);
        var ev = h.ForSession(id);
        Assert.Equal("session.start", (string?)ev[0]["type"]);
        Assert.Equal(["文1", "文2", "文3"], ev.Where(e => (string?)e["type"] == "segment.raw").Select(e => (string?)e["text"]));
        Assert.All(ev.Where(e => (string?)e["type"] == "segment.raw"), e => Assert.Equal("pending", (string?)e["polish"]));
        Assert.Equal(["整:文1", "整:文2", "整:文3"], ev.Where(e => (string?)e["type"] == "segment.polished").Select(e => (string?)e["text"]).OrderBy(x => x));
        var end = ev[^1];
        Assert.Equal("completed", (string?)end["status"]);
        Assert.Equal(3, (int?)end["lastSeq"]);
        Assert.Equal(1, h.Rig.Recorder.StopCount);
        Assert.Empty(h.Rig.Injector.Segments); // 貼り付けは通らない
        Assert.Empty(h.Rig.Injector.Texts);
        Assert.Equal(0, h.Rig.Injector.Captures); // 前景取得もしない
    }

    [Fact]
    public async Task Raw_is_sent_in_seq_order_even_when_recognition_completes_out_of_order()
    {
        var tcs = new Dictionary<int, TaskCompletionSource<RecognitionResult>> { [1] = new(), [2] = new(), [3] = new() };
        await using var h = await VoiceTestHost.StartAsync(new EngineRig((r, _) => tcs[(int)r.Pcm.Samples[0]].Task, limits: Fast()));
        var id = await h.BeginAsync();
        h.Rig.Segment(1); h.Rig.Segment(2); h.Rig.Segment(3);
        await Wait.UntilAsync(() => h.Rig.Recorder.Active, "recording");
        tcs[3].SetResult(new("文3", "cloud"));
        tcs[2].SetResult(new("文2", "cloud"));
        await Task.Delay(100);
        Assert.DoesNotContain(h.ForSession(id), e => (string?)e["type"] == "segment.raw"); // 先頭の認識待ち
        tcs[1].SetResult(new("文1", "cloud"));
        await h.UntilAsync(e => e.Count(x => (string?)x["type"] == "segment.raw") == 3, "three raws");
        await h.StopAsync(id);
        await h.UntilEndAsync(id);
        AssertContract(h, id);
        Assert.Equal(["文1", "文2", "文3"], h.ForSession(id).Where(e => (string?)e["type"] == "segment.raw").Select(e => (string?)e["text"]));
        Assert.All(h.ForSession(id).Where(e => (string?)e["type"] == "segment.raw"), e => Assert.Equal("off", (string?)e["polish"]));
    }

    [Fact]
    public async Task Raw_precedes_polish_and_polished_results_may_arrive_out_of_seq_order()
    {
        var gate = new Dictionary<string, TaskCompletionSource<PolishResult>> { ["文1"] = new(), ["文2"] = new() };
        await using var h = await VoiceTestHost.StartAsync(new EngineRig(polish: (s, _) => gate[s].Task, limits: Fast()));
        var id = await h.BeginAsync();
        h.Rig.Segment(1); h.Rig.Segment(2);
        await h.UntilAsync(e => e.Count(x => (string?)x["type"] == "segment.raw") == 2, "raws sent while polish is pending");
        Assert.DoesNotContain(h.ForSession(id), e => (string?)e["type"] == "segment.polished");
        gate["文2"].SetResult(PolishResult.Success("整2"));
        await h.UntilAsync(e => e.Any(x => (string?)x["type"] == "segment.polished"), "polished 2");
        gate["文1"].SetResult(PolishResult.Success("整1"));
        await h.StopAsync(id);
        await h.UntilEndAsync(id);
        AssertContract(h, id);
        Assert.Equal([2, 1], h.ForSession(id).Where(e => (string?)e["type"] == "segment.polished").Select(e => (int?)e["seq"]!.GetValue<int>()));
    }

    [Fact]
    public async Task Silent_failed_and_polish_failures_use_fixed_codes()
    {
        Task<RecognitionResult> Recognize(RecognitionRequest r, CancellationToken _) => (int)r.Pcm.Samples[0] switch
        {
            1 => Task.FromResult(new RecognitionResult("  ", "cloud")),
            2 => Task.FromException<RecognitionResult>(new RecognitionException("x")),
            3 => Task.FromResult(new RecognitionResult("文3", "cloud")),
            _ => Task.FromResult(new RecognitionResult("文4", "cloud")),
        };
        await using var h = await VoiceTestHost.StartAsync(new EngineRig(Recognize,
            polish: (s, _) => Task.FromResult(s == "文3" ? PolishResult.Fallback(PolishFailure.Http, "HTTP 500") : PolishResult.Success("")), limits: Fast()));
        var id = await h.BeginAsync();
        for (var i = 1; i <= 4; i++) h.Rig.Segment(i);
        await h.StopAsync(id);
        await h.UntilEndAsync(id);
        AssertContract(h, id);
        var ev = h.ForSession(id);
        Assert.Equal("silent", (string?)ev.Single(e => (int?)e["seq"] == 1 && (string?)e["type"] == "segment.raw")["status"]);
        var failed = ev.Single(e => (int?)e["seq"] == 2 && (string?)e["type"] == "segment.raw");
        Assert.Equal("failed", (string?)failed["status"]);
        Assert.Equal("recognition_failed", (string?)failed["code"]);
        Assert.Equal("polish_failed", (string?)ev.Single(e => (int?)e["seq"] == 3 && (string?)e["type"] == "segment.polished")["code"]);
        Assert.Equal("polish_failed", (string?)ev.Single(e => (int?)e["seq"] == 4 && (string?)e["type"] == "segment.polished")["code"]); // 空の整文
        Assert.Equal("completed", (string?)ev[^1]["status"]);
        Assert.Equal(4, (int?)ev[^1]["lastSeq"]);
    }

    [Fact]
    public async Task Polish_timeout_is_reported_with_the_timeout_code_and_the_session_still_completes()
    {
        await using var h = await VoiceTestHost.StartAsync(new EngineRig(polish: async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return PolishResult.Success("x"); },
            polishTimeout: TimeSpan.FromMilliseconds(150), limits: Fast()));
        var id = await h.BeginAsync();
        h.Rig.Segment(1);
        await h.StopAsync(id);
        await h.UntilEndAsync(id);
        AssertContract(h, id);
        Assert.Equal("timeout", (string?)h.ForSession(id).Single(e => (string?)e["type"] == "segment.polished")["code"]);
        Assert.Equal("completed", (string?)h.ForSession(id)[^1]["status"]);
    }

    [Fact]
    public async Task Context_reaches_only_the_polisher_and_is_capped()
    {
        await using var h = await VoiceTestHost.StartAsync(new EngineRig(polish: Ok, limits: Fast()));
        var before = new string('あ', 1500);
        var id = await h.BeginAsync(new JsonObject { ["before"] = before, ["after"] = new string('い', 300) });
        h.Rig.Segment(1);
        await h.StopAsync(id);
        await h.UntilEndAsync(id);
        var input = Assert.Single(h.Rig.PolishInputs);
        Assert.Equal(1200, input.Context!.Before.Length);
        Assert.Equal(200, input.Context.After.Length);
        Assert.Equal("文1", input.Utterance);
    }

    [Fact]
    public async Task Without_context_the_polisher_gets_the_utterance_only()
    {
        await using var h = await VoiceTestHost.StartAsync(new EngineRig(polish: Ok, limits: Fast()));
        var id = await h.BeginAsync();
        h.Rig.Segment(1);
        await h.StopAsync(id);
        await h.UntilEndAsync(id);
        Assert.Null(Assert.Single(h.Rig.PolishInputs).Context);
    }

    [Fact]
    public async Task Oversized_recognition_text_is_a_failed_segment()
    {
        await using var h = await VoiceTestHost.StartAsync(new EngineRig((_, _) => Task.FromResult(new RecognitionResult(new string('あ', 11_000), "cloud")), limits: Fast()));
        var id = await h.BeginAsync();
        h.Rig.Segment(1);
        await h.StopAsync(id);
        await h.UntilEndAsync(id);
        AssertContract(h, id);
        Assert.Equal("failed", (string?)h.ForSession(id).Single(e => (string?)e["type"] == "segment.raw")["status"]);
    }

    [Fact]
    public async Task No_audio_completes_with_last_seq_zero()
    {
        await using var h = await VoiceTestHost.StartAsync(new EngineRig(limits: Fast()));
        var id = await h.BeginAsync();
        await h.UntilAsync(e => e.Count == 1, "start");
        await h.StopAsync(id);
        await h.UntilEndAsync(id);
        AssertContract(h, id);
        Assert.Equal(0, (int?)h.ForSession(id)[^1]["lastSeq"]);
    }

    // ── 停止・取消 ──

    [Fact]
    public async Task Stop_is_idempotent_and_ignores_unknown_session_ids()
    {
        await using var h = await VoiceTestHost.StartAsync(new EngineRig(limits: Fast()));
        var id = await h.BeginAsync();
        h.Rig.Segment(1, 1);
        await h.StopAsync(VoiceTestHost.NewSessionId());
        await h.StopAsync(id);
        await h.StopAsync(id);
        await h.UntilEndAsync(id);
        await h.StopAsync(id); // 終了後
        await h.CancelAsync(id);
        await Task.Delay(100);
        AssertContract(h, id);
        Assert.Equal(1, h.Rig.Recorder.StopCount); // 末尾の回収は一度だけ
        Assert.Equal(1, h.ForSession(id).Count(e => (string?)e["type"] == "session.end"));
    }

    [Fact]
    public async Task Cancel_ends_immediately_and_late_results_are_not_sent()
    {
        var gate = new TaskCompletionSource<RecognitionResult>();
        await using var h = await VoiceTestHost.StartAsync(new EngineRig((_, _) => gate.Task, limits: Fast()));
        var id = await h.BeginAsync();
        h.Rig.Segment(1);
        await Wait.UntilAsync(() => h.Rig.Recorder.Active, "recording");
        await h.CancelAsync(id);
        await h.UntilEndAsync(id);
        gate.TrySetResult(new("文1", "cloud")); // 取消後の遅着
        await h.CancelAsync(id);
        await Task.Delay(150);
        AssertContract(h, id);
        var end = h.ForSession(id)[^1];
        Assert.Equal("cancelled", (string?)end["status"]);
        Assert.Equal(0, (int?)end["lastSeq"]);
        Assert.False(h.Rig.Recorder.Active);
    }

    [Fact]
    public async Task Drain_deadline_turns_unfinished_work_into_failed_results()
    {
        await using var h = await VoiceTestHost.StartAsync(new EngineRig(
            (r, ct) => (int)r.Pcm.Samples[0] == 1 ? Task.FromResult(new RecognitionResult("文1", "cloud")) : Task.Delay(Timeout.Infinite, ct).ContinueWith<RecognitionResult>(_ => throw new OperationCanceledException()),
            polish: async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return PolishResult.Success("x"); },
            limits: Fast(l => l with { Flush = TimeSpan.FromMilliseconds(300), SendWindow = TimeSpan.FromMilliseconds(100) })));
        var id = await h.BeginAsync();
        h.Rig.Segment(1); h.Rig.Segment(2);
        await h.StopAsync(id);
        await h.UntilEndAsync(id);
        AssertContract(h, id);
        var ev = h.ForSession(id);
        Assert.Equal("timeout", (string?)ev.Single(e => (int?)e["seq"] == 2 && (string?)e["type"] == "segment.raw")["code"]);
        Assert.Equal("timeout", (string?)ev.Single(e => (string?)e["type"] == "segment.polished")["code"]);
        Assert.Equal("completed", (string?)ev[^1]["status"]);
        Assert.Equal(2, (int?)ev[^1]["lastSeq"]);
    }

    [Fact]
    public async Task Overload_drops_excess_segments_with_the_overloaded_code()
    {
        var gate = new TaskCompletionSource<RecognitionResult>();
        await using var h = await VoiceTestHost.StartAsync(new EngineRig((r, ct) => gate.Task, limits: Fast(l => l with { MaxBufferedSegments = 1 })));
        var id = await h.BeginAsync();
        h.Rig.Segment(1); h.Rig.Segment(2); h.Rig.Segment(3);
        await Task.Delay(150);
        gate.SetResult(new("文", "cloud"));
        await h.StopAsync(id);
        await h.UntilEndAsync(id);
        AssertContract(h, id);
        var raws = h.ForSession(id).Where(e => (string?)e["type"] == "segment.raw").ToArray();
        Assert.Equal(3, raws.Length);
        Assert.Equal("overloaded", (string?)raws[1]["code"]);
        Assert.Equal("overloaded", (string?)raws[2]["code"]);
    }

    [Fact]
    public async Task Segment_count_is_capped()
    {
        await using var h = await VoiceTestHost.StartAsync(new EngineRig(limits: Fast(l => l with { MaxSegments = 2 })));
        var id = await h.BeginAsync();
        h.Rig.Segment(1); h.Rig.Segment(2); h.Rig.Segment(3);
        await h.StopAsync(id);
        await h.UntilEndAsync(id);
        AssertContract(h, id);
        Assert.Equal(2, (int?)h.ForSession(id)[^1]["lastSeq"]);
    }

    // ── 開始失敗・障害 ──

    [Fact]
    public async Task Microphone_start_failure_ends_without_session_start()
    {
        var rig = new EngineRig(limits: Fast());
        rig.Recorder.StartError = new RecorderException(RecorderErrorKind.DeviceUnavailable, "mic");
        await using var h = await VoiceTestHost.StartAsync(rig);
        var id = await h.BeginAsync();
        await h.UntilEndAsync(id);
        AssertContract(h, id);
        var ev = h.ForSession(id);
        var end = Assert.Single(ev);
        Assert.Equal("failed", (string?)end["status"]);
        Assert.Equal("microphone_unavailable", (string?)end["code"]);
        Assert.Equal(1, (int?)end["eventNo"]);
    }

    [Fact]
    public async Task Microphone_loss_during_recording_fails_the_session()
    {
        await using var h = await VoiceTestHost.StartAsync(new EngineRig(limits: Fast()));
        var id = await h.BeginAsync();
        h.Rig.Segment(1);
        await h.UntilAsync(e => e.Any(x => (string?)x["type"] == "segment.raw"), "raw 1");
        h.Rig.Recorder.RaiseFailed(new RecorderError(RecorderErrorKind.Disconnected, "lost"));
        await h.UntilEndAsync(id);
        AssertContract(h, id);
        var end = h.ForSession(id)[^1];
        Assert.Equal("failed", (string?)end["status"]);
        Assert.Equal("microphone_lost", (string?)end["code"]);
        Assert.Equal(1, (int?)end["lastSeq"]);
    }

    [Fact]
    public async Task Unsupported_recognition_engine_fails_without_recording()
    {
        var rig = new EngineRig(engine: RecognitionEngine.Local);
        await using var h = await VoiceTestHost.StartAsync(rig);
        var id = await h.BeginAsync();
        await h.UntilEndAsync(id);
        AssertContract(h, id);
        Assert.Equal("recognition_failed", (string?)h.ForSession(id)[^1]["code"]);
        Assert.Equal(0, rig.Recorder.StreamStarts);
    }

    // ── 複数セッション・接続 ──

    [Fact]
    public async Task A_second_session_on_the_same_connection_restarts_event_numbers()
    {
        await using var h = await VoiceTestHost.StartAsync(new EngineRig(limits: Fast()));
        var a = await h.BeginAsync();
        h.Rig.Segment(1);
        await h.StopAsync(a);
        await h.UntilEndAsync(a);
        var b = await h.BeginAsync();
        h.Rig.Segment(2);
        await h.StopAsync(b);
        await h.UntilEndAsync(b);
        Assert.NotEqual(a, b);
        AssertContract(h, a);
        AssertContract(h, b);
        Assert.Equal("文2", (string?)h.ForSession(b).Single(e => (string?)e["type"] == "segment.raw")["text"]);
    }

    [Fact]
    public async Task Connection_loss_releases_the_microphone_and_exits_quietly()
    {
        var rig = new EngineRig(limits: Fast());
        var h = await VoiceTestHost.StartAsync(rig);
        await h.BeginAsync();
        h.Disconnect();
        Assert.Equal(VoiceEngineHost.ExitOk, await h.Engine!.WaitAsync(TimeSpan.FromSeconds(5)));
        await Wait.UntilAsync(() => !rig.Recorder.Active && rig.Recorder.CancelCount > 0, "microphone released");
        await h.DisposeAsync();
    }

    [Fact]
    public async Task Contract_violations_from_the_host_close_the_connection_and_release_the_microphone()
    {
        foreach (var bad in new Func<VoiceTestHost, Task>[]
        {
            h => h.SendAsync(new JsonObject { ["type"] = "capture.edit", ["sessionId"] = VoiceTestHost.NewSessionId() }), // 編集 API は無い
            h => h.SendAsync(new JsonObject { ["type"] = "capture.stop", ["sessionId"] = "XYZ" }),
            h => h.SendAsync(new JsonObject { ["type"] = "capture.begin", ["sessionId"] = "A".PadLeft(32, 'A') }),
            async h => await h.BeginAsync(waitFirstEvent: false), // 同時に二セッション
        })
        {
            var rig = new EngineRig(limits: Fast());
            var h = await VoiceTestHost.StartAsync(rig);
            await h.BeginAsync();
            await bad(h);
            Assert.Equal(VoiceEngineHost.ExitOk, await h.Engine!.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(rig.Recorder.Active);
            await h.DisposeAsync();
        }
    }

    [Fact]
    public async Task Engine_does_not_send_the_token_to_a_server_whose_pid_is_not_the_parent()
    {
        var rig = new EngineRig(limits: Fast());
        await using var h = await VoiceTestHost.StartAsync(rig, serverPid: (uint)Environment.ProcessId + 1);
        Assert.Equal(VoiceEngineHost.ExitHandshake, await h.Engine!.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(h.Hello);
        Assert.Equal(0, rig.Recorder.StreamStarts);
    }

    [Fact]
    public async Task Engine_exits_when_hello_is_not_acknowledged()
    {
        var rig = new EngineRig(limits: Fast());
        await using var h = await VoiceTestHost.StartAsync(rig, acceptHello: false);
        h.Disconnect();
        Assert.Equal(VoiceEngineHost.ExitHandshake, await h.Engine!.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, rig.Recorder.StreamStarts);
    }

    // ── プロトコルの部品 ──

    [Fact]
    public void Commands_are_validated_strictly()
    {
        var id = VoiceTestHost.NewSessionId();
        Assert.Equal("capture.stop", VoiceProtocol.ParseCommand(new JsonObject { ["type"] = "capture.stop", ["sessionId"] = id })!.Type);
        Assert.Null(VoiceProtocol.ParseCommand(new JsonObject { ["type"] = "capture.stop", ["sessionId"] = id.ToUpperInvariant() }));
        Assert.Null(VoiceProtocol.ParseCommand(new JsonObject { ["type"] = "capture.stop" }));
        Assert.Null(VoiceProtocol.ParseCommand(new JsonObject { ["type"] = "edit.replace", ["sessionId"] = id }));
        Assert.Null(VoiceProtocol.ParseCommand(new JsonObject { ["type"] = "hello.ok", ["protocol"] = "voice/2" }));
        Assert.Null(VoiceProtocol.ParseCommand(new JsonObject { ["type"] = "capture.begin", ["sessionId"] = id, ["context"] = "x" }));
        Assert.NotNull(VoiceProtocol.ParseCommand(new JsonObject { ["type"] = "capture.begin", ["sessionId"] = id }));
    }

    [Fact]
    public async Task Handoff_is_read_once_and_validated()
    {
        static async Task<VoiceHandoff?> Read(JsonObject o)
        {
            var ms = new MemoryStream();
            await HaneWire.WriteAsync(ms, o, CancellationToken.None);
            ms.Position = 0;
            return await VoiceHandoff.ReadAsync(ms, CancellationToken.None);
        }
        var good = new JsonObject { ["pipeName"] = "wp-voice-abc", ["token"] = VoiceTestHost.Token, ["parentPid"] = 42 };
        Assert.Equal(new VoiceHandoff("wp-voice-abc", VoiceTestHost.Token, 42), await Read(good));
        Assert.Null(await Read(new JsonObject { ["pipeName"] = "..\\evil", ["token"] = VoiceTestHost.Token, ["parentPid"] = 42 }));
        Assert.Null(await Read(new JsonObject { ["pipeName"] = "x", ["token"] = "short", ["parentPid"] = 42 }));
        Assert.Null(await Read(new JsonObject { ["pipeName"] = "x", ["token"] = VoiceTestHost.Token }));
        Assert.Null(await VoiceHandoff.ReadAsync(new MemoryStream(), CancellationToken.None)); // 空
    }

    [Fact]
    public void The_validator_catches_contract_violations()
    {
        var id = VoiceTestHost.NewSessionId();
        JsonObject E(int no, string type, Action<JsonObject>? f = null)
        {
            var o = new JsonObject { ["type"] = type, ["sessionId"] = id, ["eventNo"] = no };
            f?.Invoke(o);
            return o;
        }
        Assert.Empty(VoiceContract.Violations([E(1, "session.start"), E(2, "session.end", o => { o["status"] = "completed"; o["lastSeq"] = 0; })], id));
        Assert.NotEmpty(VoiceContract.Violations([E(1, "session.start"), E(3, "session.end", o => { o["status"] = "completed"; o["lastSeq"] = 0; })], id)); // eventNo の飛び
        Assert.NotEmpty(VoiceContract.Violations([E(1, "session.start"), E(2, "segment.raw", o => { o["seq"] = 2; o["status"] = "silent"; }), E(3, "session.end", o => { o["status"] = "completed"; o["lastSeq"] = 2; })], id)); // seq の飛び
        Assert.NotEmpty(VoiceContract.Violations([E(1, "session.start"), E(2, "segment.polished", o => { o["seq"] = 1; o["status"] = "ok"; o["text"] = "x"; })], id)); // 原文なしの整文
        Assert.NotEmpty(VoiceContract.Violations([E(1, "session.start"), E(2, "segment.raw", o => { o["seq"] = 1; o["status"] = "ok"; o["text"] = "a"; o["polish"] = "pending"; }), E(3, "session.end", o => { o["status"] = "completed"; o["lastSeq"] = 1; })], id)); // 未終端の整文
        Assert.NotEmpty(VoiceContract.Violations([E(1, "session.start"), E(2, "session.end", o => { o["status"] = "cancelled"; o["lastSeq"] = 0; }), E(3, "session.start")], id)); // 終了後のイベント
    }
}
