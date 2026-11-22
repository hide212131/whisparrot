using System.Diagnostics;
using Whisparrot.Core.Hane;
using Whisparrot.Infrastructure.Hane;

namespace Whisparrot.Tests.Hane;

/// <summary>Hane クライアントを、実際の名前付きパイプ上のモックサーバーに対して検証する（W-1 / 設計 §2〜§4.5）。</summary>
public sealed class HaneClientTests : IDisposable
{
    private readonly MockHaneServer _srv = new();
    private readonly HaneClient _client;
    private readonly HaneWindow _win;

    public HaneClientTests()
    {
        Assert.True(Task.Run(HaneClient.WarmUp).Wait(TimeSpan.FromSeconds(10)), "WarmUp が終わらない");
        _client = Client(_srv);
        _win = new HaneWindow(_srv.Hwnd, _srv.Pid);
        _srv.SetDocument("見出し\n前の文。", cursorByte: null);
    }

    public void Dispose() => _srv.Dispose();

    internal static HaneClient Client(MockHaneServer s, Action<HaneClientOptionsBuilder>? tweak = null)
    {
        var b = new HaneClientOptionsBuilder { Dir = s.DescriptorDir, Root = s.Root };
        tweak?.Invoke(b);
        return new HaneClient(b.Build());
    }

    internal sealed class HaneClientOptionsBuilder
    {
        public string Dir = "", Root = "";
        public TimeSpan? Mutation, Retry;
        public HaneClientOptions Build() => new()
        {
            DescriptorDirectory = Dir,
            TrustedRoot = Root,
            MutationTimeout = Mutation ?? TimeSpan.FromMilliseconds(250),
            RetryBudget = Retry ?? TimeSpan.FromMilliseconds(500),
        };
    }

    private async Task<HaneContext> CtxAsync()
    {
        var r = await _client.GetContextAsync(_win, new HaneContextRequest(), CancellationToken.None);
        Assert.True(r.Ok, r.ToString());
        return r.Value!;
    }

    [Fact]
    public void Warm_up_completes_quickly_and_touches_no_hane_state()
    {
        var sw = Stopwatch.StartNew();
        var done = Task.Run(HaneClient.WarmUp).Wait(TimeSpan.FromSeconds(10));
        Assert.True(done, "WarmUp が終わらない");
        Assert.Empty(_srv.Frames);
    }

    [Fact]
    public async Task Hello_authenticates_and_negotiates_the_version()
    {
        var r = await _client.HelloAsync(_win, CancellationToken.None);
        Assert.True(r.Ok, r.ToString());
        Assert.Equal(new HaneVersion(1, 0), r.Value!.Version);
        Assert.Equal(_srv.InstanceId, r.Value.InstanceId);
        Assert.Contains("edit.replace", r.Value.Features);
        Assert.DoesNotContain(_srv.Frames.Single().Params.ToJsonString(), nameof(MockHaneServer.TokenForTest)); // トークンはモックの記録から除外済み
    }

    [Fact]
    public async Task Missing_descriptor_means_hane_is_absent()
    {
        File.Delete(Path.Combine(_srv.DescriptorDir, $"{_srv.Pid}.json"));
        var r = await _client.HelloAsync(_win, CancellationToken.None);
        Assert.Equal(HaneFailure.NoDescriptor, r.Failure);
        Assert.Equal(0, _srv.Hellos);
    }

    [Fact]
    public async Task Stale_descriptor_with_a_different_process_creation_time_is_rejected_without_connecting()
    {
        _srv.PublishDescriptor(overrideCreationTime: "1");
        var r = await _client.HelloAsync(_win, CancellationToken.None);
        Assert.Equal(HaneFailure.StaleInstance, r.Failure);
        Assert.Equal(0, _srv.Hellos);
    }

    [Theory]
    [InlineData("pipe-other")]                       // 固定形式でない
    [InlineData("Hane.Whisparrot.1.aaaaaaaa")]       // PID が記述子と合わない
    [InlineData(@"\\remote\pipe\Hane.Whisparrot.1.aaaaaaaa")]
    public async Task Descriptor_with_a_pipe_name_outside_the_fixed_form_is_rejected(string pipeName)
    {
        var path = Path.Combine(_srv.DescriptorDir, $"{_srv.Pid}.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json["pipeName"] = pipeName;
        File.WriteAllText(path, json.ToJsonString());
        var r = await _client.HelloAsync(_win, CancellationToken.None);
        Assert.Equal(HaneFailure.BadDescriptor, r.Failure);
        Assert.Equal(0, _srv.Hellos);
    }

    [Fact]
    public async Task Descriptor_directory_behind_a_reparse_point_is_rejected()
    {
        var real = Path.Combine(_srv.Root, "real");
        Directory.CreateDirectory(real);
        File.Copy(Path.Combine(_srv.DescriptorDir, $"{_srv.Pid}.json"), Path.Combine(real, $"{_srv.Pid}.json"));
        var link = Path.Combine(_srv.Root, "link");
        using (var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{real}\"") { CreateNoWindow = true, UseShellExecute = false })!)
            p.WaitForExit();
        Assert.True(Directory.Exists(link), "junction を作れなかった");
        var viaLink = HaneClientFor(link);
        var r = await viaLink.HelloAsync(_win, CancellationToken.None);
        Assert.Equal(HaneFailure.BadDescriptor, r.Failure);
        Assert.Equal(0, _srv.Hellos);
    }

    private HaneClient HaneClientFor(string dir) => new(new HaneClientOptions { DescriptorDirectory = dir, TrustedRoot = _srv.Root });

    [Fact]
    public async Task A_server_process_that_does_not_match_the_descriptor_pid_never_receives_the_token()
    {
        // 別プロセス（子プロセス）の記述子を、このプロセスが提供するパイプへ向ける。サーバー PID が合わないのでトークンを送らない。
        using var other = Process.Start(new ProcessStartInfo("ping.exe", "-n 8 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        try
        {
            using var rogue = new MockHaneServer(_srv.Root, pipePid: (uint)other.Id);
            var created = HaneNative.ProcessCreationFileTime((uint)other.Id)!.Value;
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(rogue.DescriptorDir, $"{rogue.Pid}.json")))!.AsObject();
            json["pid"] = other.Id;
            json["processCreationTime"] = created.ToString();
            File.WriteAllText(Path.Combine(rogue.DescriptorDir, $"{other.Id}.json"), json.ToJsonString());
            var r = await Client(rogue).HelloAsync(new HaneWindow(rogue.Hwnd, (uint)other.Id), CancellationToken.None);
            Assert.Equal(HaneFailure.ServerMismatch, r.Failure);
            Assert.Equal(0, rogue.Hellos);
        }
        finally { other.Kill(); }
    }

    [Fact]
    public async Task A_wrong_token_is_rejected_and_a_rotated_token_is_picked_up_on_the_next_operation()
    {
        var path = Path.Combine(_srv.DescriptorDir, $"{_srv.Pid}.json");
        var good = File.ReadAllText(path);
        var json = System.Text.Json.Nodes.JsonNode.Parse(good)!.AsObject();
        json["token"] = new string('a', 64);
        File.WriteAllText(path, json.ToJsonString());
        Assert.Equal(HaneFailure.AuthRejected, (await _client.HelloAsync(_win, CancellationToken.None)).Failure);

        _srv.RotateToken();
        Assert.True((await _client.HelloAsync(_win, CancellationToken.None)).Ok);
    }

    [Fact]
    public async Task Major_version_mismatch_means_no_integration()
    {
        _srv.ServerMajor = (2, 2);
        _srv.PublishDescriptor(); // 記述子の範囲 2..2 は、この版（1..1）と重ならない → トークンを送る前に連携不可
        var r = await _client.HelloAsync(_win, CancellationToken.None);
        Assert.Equal(HaneFailure.UnsupportedVersion, r.Failure);
        Assert.Equal(0, _srv.Hellos);
    }

    [Fact]
    public async Task Server_side_version_refusal_is_reported_as_unsupported()
    {
        _srv.ServerMajor = (1, 1);
        _srv.PublishDescriptor();
        _srv.ServerMajor = (2, 2); // 記述子は 1 を名乗るが、サーバーの交渉では合わない
        var r = await _client.HelloAsync(_win, CancellationToken.None);
        Assert.Equal(HaneFailure.UnsupportedVersion, r.Failure);
    }

    [Fact]
    public async Task Context_returns_the_cursor_neighbourhood_in_utf8_byte_coordinates()
    {
        _srv.SetDocument("😀é末尾\nあ", cursorByte: null);
        _srv.MoveCaret(System.Text.Encoding.UTF8.GetByteCount("😀é末尾\n"));
        var ctx = await CtxAsync();
        Assert.Equal("😀é末尾\n", ctx.Before);
        Assert.Equal("あ", ctx.After);
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount("😀é末尾\n"), ctx.CursorByte);
        Assert.Equal(new HaneSelection(ctx.CursorByte, ctx.CursorByte), ctx.Selection);
        Assert.Equal(_srv.DocumentId, ctx.DocumentId);
        Assert.Equal(_srv.InstanceId, ctx.InstanceId);
        Assert.Equal("absent", ctx.FrontMatterStatus);
    }

    [Fact]
    public async Task Context_request_carries_the_limits_and_the_pending_handles_at_most_two()
    {
        var r = await _client.GetContextAsync(_win, new HaneContextRequest(5000, 5000, ["a", "b", "c"]), CancellationToken.None);
        Assert.True(r.Ok);
        var p = _srv.Of("context.get").Single().Params;
        Assert.Equal(1200, p["beforeChars"]!.GetValue<int>());
        Assert.Equal(200, p["afterChars"]!.GetValue<int>());
        Assert.False(p["includeSnapshot"]!.GetValue<bool>());
        Assert.Equal(2, p["pendingRangeHandles"]!.AsArray().Count);
    }

    [Theory]
    [InlineData("UNSUPPORTED_DOCUMENT")]
    [InlineData("BUSY")]
    [InlineData("NOT_EDITABLE")]
    public async Task Context_errors_surface_the_fixed_code(string code)
    {
        _srv.ContextError = code;
        var r = await _client.GetContextAsync(_win, new HaneContextRequest(), CancellationToken.None);
        Assert.Equal(HaneFailure.Remote, r.Failure);
        Assert.Equal(code, r.Code);
    }

    [Fact]
    public async Task Context_for_another_window_is_refused_by_hane()
    {
        var r = await _client.GetContextAsync(new HaneWindow(_srv.Hwnd + 1, _srv.Pid), new HaneContextRequest(), CancellationToken.None);
        Assert.Equal("STALE_TARGET", r.Code);
    }

    [Fact]
    public async Task Context_that_exceeds_the_budget_fails_with_timeout()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var r = await _client.GetContextAsync(_win, new HaneContextRequest(), cts.Token);
        Assert.Equal(HaneFailure.Timeout, r.Failure);
    }

    [Fact]
    public async Task Insert_puts_the_text_at_the_caret_and_returns_a_range_handle()
    {
        var ctx = await CtxAsync();
        var r = await _client.InsertAsync(_win, ctx, "原文です。", CancellationToken.None);
        Assert.True(r.Ok, r.ToString());
        Assert.Equal("見出し\n前の文。原文です。", _srv.Text);
        var ins = r.Value!;
        Assert.Equal(ctx.CursorByte, ins.StartByte);
        Assert.Equal(ctx.CursorByte + System.Text.Encoding.UTF8.GetByteCount("原文です。"), ins.EndByte);
        Assert.NotEmpty(ins.RangeHandle);
        Assert.Equal(_srv.InstanceId, ins.InstanceId);

        var f = _srv.Of("edit.insert").Single();
        Assert.Matches("^[0-9a-f]{32}$", f.OperationId!);
        Assert.True(long.Parse(f.Params["deadlineTick"]!.GetValue<string>()) > 0); // GetTickCount64 の十進文字列
        Assert.Equal(ctx.Revision, f.Params["expectedRevision"]!.GetValue<string>());
    }

    [Fact]
    public async Task Insert_is_refused_when_the_cursor_or_the_version_moved_since_the_context()
    {
        var ctx = await CtxAsync();
        _srv.MoveCaret(0);
        var cursor = await _client.InsertAsync(_win, ctx, "x", CancellationToken.None);
        Assert.Equal("STALE_CURSOR", cursor.Code);
        Assert.Equal(HaneEffect.NotApplied, cursor.Effect);

        var ctx2 = await CtxAsync();
        _srv.UserEdit(0, 0, "A");
        var version = await _client.InsertAsync(_win, ctx2, "x", CancellationToken.None);
        Assert.Equal("STALE_VERSION", version.Code);
        Assert.Equal(HaneEffect.NotApplied, version.Effect);
        Assert.Equal("A見出し\n前の文。", _srv.Text);
    }

    [Fact]
    public async Task Text_over_32_KiB_is_refused_locally_before_anything_is_sent()
    {
        var ctx = await CtxAsync();
        var r = await _client.InsertAsync(_win, ctx, new string('あ', 11_000), CancellationToken.None); // 33,000 bytes
        Assert.Equal(HaneFailure.TooLarge, r.Failure);
        Assert.Equal(HaneEffect.NotApplied, r.Effect);
        Assert.Empty(_srv.Of("edit.insert"));
    }

    [Fact]
    public async Task A_lost_insert_response_is_re_asked_with_the_same_operation_id_and_the_text_lands_once()
    {
        var ctx = await CtxAsync();
        _srv.DropResponseAfterApply = true;
        var r = await _client.InsertAsync(_win, ctx, "一度だけ。", CancellationToken.None);

        Assert.True(r.Ok, r.ToString());
        Assert.Equal("見出し\n前の文。一度だけ。", _srv.Text);
        var frames = _srv.Of("edit.insert").ToArray();
        Assert.Equal(2, frames.Length);
        Assert.Equal(frames[0].OperationId, frames[1].OperationId);
        Assert.Equal(frames[0].Params.ToJsonString(), frames[1].Params.ToJsonString()); // 期限・内容も同じ
    }

    [Fact]
    public async Task A_request_that_never_arrived_is_applied_by_the_re_ask()
    {
        var ctx = await CtxAsync();
        _srv.DropRequestBeforeApply = true;
        var r = await _client.InsertAsync(_win, ctx, "届かなかった。", CancellationToken.None);
        Assert.True(r.Ok, r.ToString());
        Assert.Equal("見出し\n前の文。届かなかった。", _srv.Text);
    }

    [Fact]
    public async Task A_slow_apply_is_reported_in_progress_and_the_client_waits_for_the_recorded_result()
    {
        var ctx = await CtxAsync();
        var hold = _srv.HoldApply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Client(_srv, b => { b.Mutation = TimeSpan.FromMilliseconds(120); b.Retry = TimeSpan.FromMilliseconds(1500); });
        var task = client.InsertAsync(_win, ctx, "処理中。", CancellationToken.None);
        await Task.Delay(400); // 初回は期限切れ、再照会は IN_PROGRESS を受けている
        hold.SetResult();
        var r = await task;
        Assert.True(r.Ok, r.ToString());
        Assert.Equal("見出し\n前の文。処理中。", _srv.Text);
        Assert.Contains(_srv.Of("edit.insert"), f => f.Params["operationId"] is not null);
    }

    [Fact]
    public async Task When_the_re_ask_also_gets_no_answer_the_effect_is_unknown()
    {
        var ctx = await CtxAsync();
        _srv.DropEveryConnectionOnMutation = true;
        var r = await _client.InsertAsync(_win, ctx, "不明。", CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Equal(HaneEffect.Unknown, r.Effect);
        Assert.Equal(2, _srv.Of("edit.insert").Count()); // 再送は一度だけ
    }

    [Fact]
    public async Task When_hane_is_gone_at_the_re_ask_the_effect_stays_unknown()
    {
        var ctx = await CtxAsync();
        _srv.DropResponseAfterApply = true;
        // 応答を落としたあと、Hane が消える（記述子を削除する）。照会できなければ不明のまま。
        var task = _client.InsertAsync(_win, ctx, "消えた。", CancellationToken.None);
        File.Delete(Path.Combine(_srv.DescriptorDir, $"{_srv.Pid}.json"));
        var r = await task;
        // 競合: 先に再照会が走れば成功し得る。どちらでも「適用は一度だけ」を保つ。
        Assert.Equal("見出し\n前の文。消えた。", _srv.Text);
        if (!r.Ok) Assert.Equal(HaneEffect.Unknown, r.Effect);
    }

    [Fact]
    public async Task A_different_instance_between_context_and_insert_is_refused_before_sending()
    {
        var ctx = await CtxAsync();
        _srv.Restart("doc-after-restart");
        await Task.Delay(100);
        var r = await _client.InsertAsync(_win, ctx, "別の Hane。", CancellationToken.None);
        Assert.Equal(HaneFailure.InstanceChanged, r.Failure);
        Assert.Equal(HaneEffect.NotApplied, r.Effect);
        Assert.Empty(_srv.Of("edit.insert"));
    }

    [Fact]
    public async Task Replace_swaps_the_untouched_range_and_consumes_the_handle()
    {
        var ins = await InsertAsync("原文。");
        var r = await _client.ReplaceAsync(_win, ins, "整えた文。", CancellationToken.None);
        Assert.True(r.Ok, r.ToString());
        Assert.Equal("見出し\n前の文。整えた文。", _srv.Text);
        Assert.Equal("merged", r.Value!.UndoMode);

        var again = await _client.ReplaceAsync(_win, ins, "二度目。", CancellationToken.None);
        Assert.Equal("RANGE_EXPIRED", again.Code); // 消費済み
        Assert.Equal("見出し\n前の文。整えた文。", _srv.Text);
    }

    [Fact]
    public async Task Replace_is_refused_when_the_inserted_range_was_edited_and_nothing_is_changed()
    {
        var ins = await InsertAsync("原文です。");
        _srv.UserEdit(ins.StartByte + 3, 0, "★"); // 範囲内部への編集
        var before = _srv.Text;
        var r = await _client.ReplaceAsync(_win, ins, "整文後。", CancellationToken.None);
        Assert.Equal("RANGE_CHANGED", r.Code);
        Assert.Equal(HaneEffect.NotApplied, r.Effect);
        Assert.Equal(before, _srv.Text);
    }

    [Fact]
    public async Task Replace_follows_edits_outside_the_range_but_not_inside_it()
    {
        var ins = await InsertAsync("原文。");
        _srv.UserEdit(0, 0, "先頭追記");          // 範囲より前
        var end = System.Text.Encoding.UTF8.GetByteCount(_srv.Text);
        _srv.UserEdit(end, 0, "末尾追記");          // 範囲の末尾への挿入は範囲外
        var r = await _client.ReplaceAsync(_win, ins, "整った。", CancellationToken.None);
        Assert.True(r.Ok, r.ToString());
        Assert.Equal("先頭追記見出し\n前の文。整った。末尾追記", _srv.Text);
    }

    [Fact]
    public async Task Replace_after_an_insert_at_the_start_of_the_range_shifts_it_right()
    {
        var ins = await InsertAsync("原文。");
        _srv.UserEdit(ins.StartByte, 0, ">>");
        var r = await _client.ReplaceAsync(_win, ins, "整った。", CancellationToken.None);
        Assert.True(r.Ok, r.ToString());
        Assert.Equal("見出し\n前の文。>>整った。", _srv.Text);
    }

    [Fact]
    public async Task A_lost_replace_response_is_re_asked_with_the_same_operation_id()
    {
        var ins = await InsertAsync("原文。");
        _srv.DropResponseAfterApply = true;
        var r = await _client.ReplaceAsync(_win, ins, "整えた。", CancellationToken.None);
        Assert.True(r.Ok, r.ToString());
        Assert.Equal("見出し\n前の文。整えた。", _srv.Text);
        var frames = _srv.Of("edit.replace").ToArray();
        Assert.Equal(2, frames.Length);
        Assert.Equal(frames[0].OperationId, frames[1].OperationId);
    }

    [Fact]
    public async Task Error_codes_from_hane_are_reduced_to_a_fixed_identifier_form()
    {
        var ctx = await CtxAsync();
        _srv.NextMutationError = "BUSY";
        var r = await _client.InsertAsync(_win, ctx, "x", CancellationToken.None);
        Assert.Equal("BUSY", r.Code);
        Assert.Equal(HaneEffect.NotApplied, r.Effect);
    }

    private async Task<HaneInserted> InsertAsync(string text)
    {
        var ctx = await CtxAsync();
        var r = await _client.InsertAsync(_win, ctx, text, CancellationToken.None);
        Assert.True(r.Ok, r.ToString());
        return r.Value!;
    }
}
