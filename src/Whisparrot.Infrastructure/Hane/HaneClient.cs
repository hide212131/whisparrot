using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Whisparrot.Core.Hane;

namespace Whisparrot.Infrastructure.Hane;

/// <summary>
/// <see cref="IHaneClient"/> の名前付きパイプ実装（設計 §2〜§4.5）。操作ごとに接続・認証し、要求の応答を返して閉じる。
/// 挿入・差し替えは操作 ID と期限を付け、応答が不明なら同じ操作 ID・期限・内容で一度だけ（追加の予算内で）照会する。
/// 文書本文・トークン・例外文字列はログにも結果にも持ち込まない。
/// </summary>
public sealed class HaneClient : IHaneClient
{
    private readonly HaneClientOptions _o;

    public HaneClient() : this(new HaneClientOptions()) { }

    internal HaneClient(HaneClientOptions options) => _o = options;

    /// <summary>
    /// JIT・型初期化・パイプ I/O の初回コストを先に払う（初回の文脈取得を 150 ms の予算に収めるため。設計 §4.1）。
    /// このプロセス内の使い捨てパイプで 1 フレームを往復するだけで、Hane の記述子・パイプには触れない。失敗は無視する。
    /// </summary>
    public static void WarmUp()
    {
        try
        {
            var name = "whisparrot.warmup." + Guid.NewGuid().ToString("N");
            using var server = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.InOut, 1, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
            using var client = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.Anonymous);
            var accept = server.WaitForConnectionAsync();
            client.ConnectAsync(1000).GetAwaiter().GetResult();
            accept.GetAwaiter().GetResult();
            _ = HaneNative.ServerProcessId(client.SafePipeHandle);
            _ = HaneNative.ProcessCreationFileTime((uint)Environment.ProcessId);
            var frame = new JsonObject { ["id"] = 1, ["method"] = "hello", ["params"] = new JsonObject { ["text"] = "warmup" } };
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var read = HaneWire.ReadAsync(server, limit.Token); // 既定は緩衝なしのパイプ。読み手を先に待たせてから書く
            HaneWire.WriteAsync(client, frame, limit.Token).GetAwaiter().GetResult();
            _ = read.GetAwaiter().GetResult();
            _ = HaneDescriptorReader.Read(Path.Combine(Path.GetTempPath(), "whisparrot-warmup-none"), Path.GetTempPath(), 0);
        }
        catch
        {
            // 温めるだけ。
        }
    }

    /// <summary>診断用（<c>--hane-probe</c>）: 記述子の読取・パイプ接続・認証・版交渉だけを行う。文書には触れない。</summary>
    public async Task<HaneResult<HaneHello>> HelloAsync(HaneWindow window, CancellationToken ct)
    {
        try
        {
            var (conn, failure) = await HaneConnection.OpenAsync(window, _o, ct).ConfigureAwait(false);
            if (conn is null) return HaneResult<HaneHello>.Fail(failure);
            using (conn) return HaneResult<HaneHello>.Success(conn.Hello);
        }
        catch (OperationCanceledException) { return HaneResult<HaneHello>.Fail(HaneFailure.Timeout); }
        catch (Exception ex) when (IsIo(ex)) { return HaneResult<HaneHello>.Fail(Classify(ex)); }
    }

    public async Task<HaneResult<HaneContext>> GetContextAsync(HaneWindow window, HaneContextRequest request, CancellationToken ct)
    {
        try
        {
            var (conn, failure) = await HaneConnection.OpenAsync(window, _o, ct).ConfigureAwait(false);
            if (conn is null) return HaneResult<HaneContext>.Fail(failure);
            using (conn)
            {
                var args = new JsonObject
                {
                    ["hwnd"] = window.Hwnd,
                    ["beforeChars"] = Math.Clamp(request.BeforeChars, 0, 1200),
                    ["afterChars"] = Math.Clamp(request.AfterChars, 0, 200),
                    ["includeSnapshot"] = false,
                };
                if (request.PendingRangeHandles is { Count: > 0 } handles)
                    args["pendingRangeHandles"] = new JsonArray(handles.Take(HaneContextRequest.MaxPendingHandles).Select(h => (JsonNode?)JsonValue.Create(h)).ToArray());

                var resp = await conn.RequestAsync("context.get", args, ct).ConfigureAwait(false);
                if (resp["error"] is JsonObject err) return HaneResult<HaneContext>.Fail(HaneFailure.Remote, HaneJson.SafeCode(err), HaneJson.Effect(err));
                var ctx = resp["result"] is JsonObject r ? ParseContext(r, conn.Hello.InstanceId) : null;
                return ctx is null ? HaneResult<HaneContext>.Fail(HaneFailure.Protocol) : HaneResult<HaneContext>.Success(ctx);
            }
        }
        catch (OperationCanceledException) { return HaneResult<HaneContext>.Fail(HaneFailure.Timeout); }
        catch (Exception ex) when (IsIo(ex)) { return HaneResult<HaneContext>.Fail(Classify(ex)); }
    }

    public Task<HaneResult<HaneInserted>> InsertAsync(HaneWindow window, HaneContext context, string text, CancellationToken ct)
    {
        if (HaneWire.Utf8Length(text) > HaneWire.MaxTextBytes) return Task.FromResult(HaneResult<HaneInserted>.Fail(HaneFailure.TooLarge));
        var args = new JsonObject
        {
            ["hwnd"] = window.Hwnd,
            ["documentId"] = context.DocumentId,
            ["expectedRevision"] = context.Revision,
            ["expectedSelection"] = new JsonObject { ["anchorByte"] = context.Selection.AnchorByte, ["activeByte"] = context.Selection.ActiveByte },
            ["text"] = text,
        };
        return MutateAsync(window, context.InstanceId, "edit.insert", args, (r, now) => ParseInserted(r, context.InstanceId, now), ct);
    }

    public Task<HaneResult<HaneReplaced>> ReplaceAsync(HaneWindow window, HaneInserted inserted, string replacement, CancellationToken ct)
    {
        if (HaneWire.Utf8Length(replacement) > HaneWire.MaxTextBytes) return Task.FromResult(HaneResult<HaneReplaced>.Fail(HaneFailure.TooLarge));
        var args = new JsonObject
        {
            ["hwnd"] = window.Hwnd,
            ["documentId"] = inserted.DocumentId,
            ["rangeHandle"] = inserted.RangeHandle,
            ["insertedRevision"] = inserted.InsertedRevision,
            ["expectedTextHash"] = inserted.ExpectedTextHash,
            ["replacement"] = replacement,
        };
        return MutateAsync(window, inserted.InstanceId, "edit.replace", args, (r, _) => ParseReplaced(r), ct);
    }

    // ── 変異要求（設計 §3.4 / §4.5）──

    private enum StepKind { Result, Error, Lost }

    private readonly record struct Step(StepKind Kind, JsonObject? Result = null, string? Code = null, HaneEffect Effect = HaneEffect.Unknown);

    private async Task<HaneResult<T>> MutateAsync<T>(HaneWindow window, string instanceId, string method, JsonObject args, Func<JsonObject, long, T?> parse, CancellationToken ct)
        where T : class
    {
        // 操作 ID・期限・内容はここで一度だけ決め、再照会でも変えない。
        args["operationId"] = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        args["deadlineTick"] = (_o.TickMs() + (long)_o.MutationTimeout.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture);

        // 初回。接続・認証の失敗や別インスタンスは、要求を送る前なので未適用が確か。
        HaneConnection? conn = null;
        Step first;
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(_o.MutationTimeout);
            try
            {
                var (c, failure) = await HaneConnection.OpenAsync(window, _o, cts.Token).ConfigureAwait(false);
                if (c is null) return HaneResult<T>.Fail(failure);
                conn = c;
                if (conn.Hello.InstanceId != instanceId) return HaneResult<T>.Fail(HaneFailure.InstanceChanged);
                first = await SendAsync(conn, method, args, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return HaneResult<T>.Fail(HaneFailure.Timeout); }
            catch (Exception ex) when (IsIo(ex)) { return HaneResult<T>.Fail(Classify(ex)); }
            finally { conn?.Dispose(); }
        }

        switch (first.Kind)
        {
            case StepKind.Result:
                return Finish(first.Result!, parse);
            case StepKind.Error when first.Effect == HaneEffect.NotApplied:
                return HaneResult<T>.Fail(HaneFailure.Remote, first.Code, HaneEffect.NotApplied);
        }

        // 応答が不明（喪失・タイムアウト・不明な拒否・処理中）。同じ Hane（同じインスタンス）へ、同じ操作 ID・期限・内容で一度だけ再送して照会する。
        using var retry = CancellationTokenSource.CreateLinkedTokenSource(ct);
        retry.CancelAfter(_o.RetryBudget);
        HaneConnection? rc = null;
        try
        {
            var (c, failure) = await HaneConnection.OpenAsync(window, _o, retry.Token).ConfigureAwait(false);
            if (c is null) return HaneResult<T>.Fail(failure, effect: HaneEffect.Unknown);
            rc = c;
            if (rc.Hello.InstanceId != instanceId) return HaneResult<T>.Fail(HaneFailure.InstanceChanged, effect: HaneEffect.Unknown);
            while (true)
            {
                var step = await SendAsync(rc, method, args, retry.Token).ConfigureAwait(false);
                switch (step.Kind)
                {
                    case StepKind.Result:
                        return Finish(step.Result!, parse);
                    case StepKind.Error when step.Effect == HaneEffect.NotApplied:
                        return HaneResult<T>.Fail(HaneFailure.Remote, step.Code, HaneEffect.NotApplied);
                    case StepKind.Error when step.Code == "IN_PROGRESS":
                        await Task.Delay(_o.InProgressPoll, retry.Token).ConfigureAwait(false); // 処理中。予算の範囲で照会し直す
                        continue;
                    default:
                        return HaneResult<T>.Fail(step.Kind == StepKind.Error ? HaneFailure.Remote : HaneFailure.Timeout, step.Code, HaneEffect.Unknown);
                }
            }
        }
        catch (OperationCanceledException) { return HaneResult<T>.Fail(HaneFailure.Timeout, effect: HaneEffect.Unknown); }
        catch (Exception ex) when (IsIo(ex)) { return HaneResult<T>.Fail(Classify(ex), effect: HaneEffect.Unknown); }
        finally { rc?.Dispose(); }

        HaneResult<T> Finish(JsonObject result, Func<JsonObject, long, T?> p) =>
            p(result, _o.TickMs()) is { } v
                ? HaneResult<T>.Success(v)
                : HaneResult<T>.Fail(HaneFailure.Protocol, effect: HaneEffect.Unknown); // 適用されたが応答を読めない
    }

    private static async Task<Step> SendAsync(HaneConnection conn, string method, JsonObject args, CancellationToken ct)
    {
        try
        {
            var resp = await conn.RequestAsync(method, args, ct).ConfigureAwait(false);
            if (resp["error"] is JsonObject err) return new Step(StepKind.Error, Code: HaneJson.SafeCode(err), Effect: HaneJson.Effect(err));
            return resp["result"] is JsonObject r ? new Step(StepKind.Result, r) : new Step(StepKind.Lost);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new Step(StepKind.Lost); }
        catch (Exception ex) when (IsIo(ex)) { return new Step(StepKind.Lost); }
    }

    private static bool IsIo(Exception ex) =>
        ex is IOException or HaneWire.FrameException or HaneIoException or ObjectDisposedException or InvalidOperationException;

    private static HaneFailure Classify(Exception ex) => ex switch
    {
        HaneIoException h => h.Failure,
        HaneWire.FrameException { TooLarge: true } => HaneFailure.TooLarge,
        _ => HaneFailure.Protocol,
    };

    // ── 応答の解釈 ──

    private static HaneContext? ParseContext(JsonObject r, string instanceId)
    {
        var documentId = HaneJson.Str(r, "documentId");
        var revision = HaneJson.Decimal(r, "revision");
        var cursor = HaneJson.Long(r, "cursorByte");
        if (string.IsNullOrEmpty(documentId) || revision is null || cursor is not { } c) return null;

        var selection = r["selection"] is JsonObject s && HaneJson.Long(s, "anchorByte") is { } a && HaneJson.Long(s, "activeByte") is { } act
            ? new HaneSelection(a, act)
            : new HaneSelection(c, c);
        var fm = r["frontMatter"] as JsonObject;
        var pending = new List<HanePendingRange>();
        if (r["pendingRanges"] is JsonArray pa)
        {
            foreach (var item in pa.Take(HaneContextRequest.MaxPendingHandles))
            {
                if (item is JsonObject po && HaneJson.Str(po, "rangeHandle") is { } h)
                    pending.Add(new HanePendingRange(h, HaneJson.Bool(po, "tracked", false), HaneJson.Long(po, "startByte") ?? -1, HaneJson.Long(po, "endByte") ?? -1));
            }
        }
        return new HaneContext(
            instanceId, documentId, revision, c, selection,
            HaneJson.Str(r, "title") ?? "", HaneJson.Bool(r, "isUntitled", false),
            fm is null ? "absent" : HaneJson.Str(fm, "status") ?? "unavailable", fm is null ? null : HaneJson.Str(fm, "text"),
            HaneJson.Str(r, "before") ?? "", HaneJson.Str(r, "after") ?? "",
            HaneJson.Bool(r, "beforeTruncated", false), HaneJson.Bool(r, "afterTruncated", false),
            HaneJson.Long(r, "beforeStartByte"), HaneJson.Long(r, "afterStartByte"), pending);
    }

    private static HaneInserted? ParseInserted(JsonObject r, string instanceId, long nowTick)
    {
        var documentId = HaneJson.Str(r, "documentId");
        var rev = HaneJson.Decimal(r, "insertedRevision");
        var handle = HaneJson.Str(r, "rangeHandle");
        var hash = HaneJson.Str(r, "expectedTextHash");
        if (string.IsNullOrEmpty(documentId) || rev is null || string.IsNullOrEmpty(handle) || string.IsNullOrEmpty(hash)) return null;
        if (r["range"] is not JsonObject range || HaneJson.Long(range, "startByte") is not { } start || HaneJson.Long(range, "endByte") is not { } end) return null;
        var expiresMs = Math.Clamp(HaneJson.Long(r, "expiresInMs") ?? 120_000, 0, 600_000);
        return new HaneInserted(instanceId, documentId, rev, start, end, handle, hash, nowTick + expiresMs);
    }

    private static HaneReplaced? ParseReplaced(JsonObject r)
    {
        var documentId = HaneJson.Str(r, "documentId");
        var rev = HaneJson.Decimal(r, "revision");
        if (string.IsNullOrEmpty(documentId) || rev is null) return null;
        var range = r["range"] as JsonObject;
        return new HaneReplaced(documentId, rev, range is null ? -1 : HaneJson.Long(range, "startByte") ?? -1, range is null ? -1 : HaneJson.Long(range, "endByte") ?? -1, HaneJson.Str(r, "undoMode"));
    }
}

/// <summary>設定から連携の構えを作る（整文の <c>SettingsTextPolisher</c> と同じ作法）。OFF・設定不能は <see cref="HaneSetup.Off"/>。</summary>
public sealed class SettingsHaneLink(Func<Whisparrot.Core.Settings.AppSettings> settings, IHaneClient client) : IHaneLink
{
    public HaneSetup Prepare() =>
        settings().Hane is { Enabled: true } h
            ? new HaneSetup.Ready(client, h.InsertRawFirst) { ContextAllowed = () => settings().Hane is { Enabled: true, SendContextToPolish: true } }
            : new HaneSetup.Off();
}
