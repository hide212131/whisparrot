using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Whisparrot.Infrastructure.Hane;

namespace Whisparrot.Tests.Hane;

/// <summary>
/// テスト専用の Hane パイプサーバー（設計 §2〜§3 のプロトコルのモック。製品コードには含めない）。
/// 記述子の公開、トークン認証、版交渉、<c>context.get</c> / <c>edit.insert</c> / <c>edit.replace</c>、操作 ID による再送の識別、
/// 範囲の追跡と未変更の確認を、UTF-8 バイト座標の小さな文書モデルの上で行う。障害（応答喪失・処理中・拒否）を注入できる。
/// </summary>
internal sealed class MockHaneServer : IDisposable
{
    public sealed record Frame(string Method, string? OperationId, JsonObject Params);

    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Frame> _frames = [];
    private readonly Dictionary<string, (string Fingerprint, JsonNode? Response, bool Done)> _ops = [];
    private readonly Dictionary<string, Handle> _handles = [];
    private readonly List<(long Rev, long Start, long Delete, long Insert)> _log = [];
    private readonly List<NamedPipeServerStream> _live = [];
    private readonly Task _accept;
    private byte[] _doc = [];
    private readonly byte[] _token = RandomNumberGenerator.GetBytes(32);
    private string _tokenText;

    private sealed record Handle(string DocumentId, long Revision, long Start, long End, byte[] Text, string Hash, long ExpiresAt);

    public string Root { get; }
    public string DescriptorDir { get; }
    public uint Pid { get; }
    public string InstanceId { get; private set; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
    public string PipeName { get; private set; }
    public long Hwnd { get; init; } = 0x4242;
    public string DocumentId { get; set; } = "doc-1";
    public long Revision { get; private set; } = 1;
    public long Anchor { get; set; }
    public long Active { get; set; }

    // 設定・障害注入
    public (int Min, int Max) ServerMajor { get; set; } = (1, 1);
    public string? ContextError { get; set; }        // 例: UNSUPPORTED_DOCUMENT / BUSY / NOT_EDITABLE
    public string? NextMutationError { get; set; }   // 次の変異要求を未適用の拒否（effect=not_applied）にする
    public bool DropResponseAfterApply { get; set; } // 次の変異要求を適用・記録して、応答せず接続を閉じる
    public bool DropRequestBeforeApply { get; set; } // 次の変異要求を適用せず、応答せず接続を閉じる
    public bool DropEveryConnectionOnMutation { get; set; } // 以後の変異要求は常に応答せず閉じる（再照会も失敗させる）
    public TaskCompletionSource? HoldApply { get; set; }    // 適用を保留する（その間の再送は IN_PROGRESS）
    public int Hellos { get; private set; }
    public int Mutations { get; private set; }

    public MockHaneServer(string? root = null, uint? pipePid = null)
    {
        Root = root ?? Path.Combine(Path.GetTempPath(), "whisparrot-hane-" + Guid.NewGuid().ToString("N"));
        DescriptorDir = Path.Combine(Root, "Hane", "integration", "whisparrot");
        Directory.CreateDirectory(DescriptorDir);
        Pid = (uint)Environment.ProcessId;
        _tokenText = Convert.ToHexStringLower(_token);
        PipeName = $"Hane.Whisparrot.{pipePid ?? Pid}.{InstanceId}";
        PublishDescriptor();
        _accept = Task.Run(AcceptLoop);
    }

    public IReadOnlyList<Frame> Frames { get { lock (_gate) return _frames.ToArray(); } }

    public IEnumerable<Frame> Of(string method) => Frames.Where(f => f.Method == method);

    // ── 文書モデル ──

    public string Text { get { lock (_gate) return Encoding.UTF8.GetString(_doc); } }

    public void SetDocument(string text, long? cursorByte = null)
    {
        lock (_gate)
        {
            _doc = Encoding.UTF8.GetBytes(text);
            Revision++;
            Anchor = Active = cursorByte ?? _doc.Length;
        }
    }

    /// <summary>利用者の編集（バイト座標）。版が進み、範囲の追跡に記録される。</summary>
    public void UserEdit(long start, long delete, string insert, bool moveCaretToEnd = false)
    {
        lock (_gate)
        {
            var ins = Encoding.UTF8.GetBytes(insert);
            ApplyEdit(start, delete, ins);
            if (moveCaretToEnd) Anchor = Active = start + ins.Length;
        }
    }

    public void MoveCaret(long byteOffset)
    {
        lock (_gate) { Anchor = Active = byteOffset; }
    }

    private void ApplyEdit(long start, long delete, byte[] insert)
    {
        var next = new byte[_doc.Length - delete + insert.Length];
        Array.Copy(_doc, 0, next, 0, start);
        Array.Copy(insert, 0, next, start, insert.Length);
        Array.Copy(_doc, start + delete, next, start + insert.Length, _doc.Length - start - delete);
        _doc = next;
        Revision++;
        _log.Add((Revision, start, delete, insert.Length));
        // キャレットの座標変換（編集位置より後ろなら移動）。
        if (Active >= start + delete) Active += insert.Length - delete;
        else if (Active > start) Active = start;
        if (Anchor >= start + delete) Anchor += insert.Length - delete;
        else if (Anchor > start) Anchor = start;
    }

    // ── 記述子 ──

    public void PublishDescriptor(string? overrideCreationTime = null, uint? overridePid = null)
    {
        var created = HaneNative.ProcessCreationFileTime(Pid)!.Value;
        var json = new JsonObject
        {
            ["pid"] = overridePid ?? Pid,
            ["processCreationTime"] = overrideCreationTime ?? created.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["instanceId"] = InstanceId,
            ["pipeName"] = PipeName,
            ["token"] = _tokenText,
            ["protocol"] = new JsonObject
            {
                ["min"] = new JsonObject { ["major"] = ServerMajor.Min, ["minor"] = 0 },
                ["max"] = new JsonObject { ["major"] = ServerMajor.Max, ["minor"] = 0 },
            },
        };
        var path = Path.Combine(DescriptorDir, $"{overridePid ?? Pid}.json");
        File.WriteAllText(path + ".tmp", json.ToJsonString());
        File.Move(path + ".tmp", path, overwrite: true);
    }

    /// <summary>トークンを更新する（起動・再有効化ごとに更新される。設計 §2）。クライアントは操作ごとに記述子を読み直す。</summary>
    public void RotateToken()
    {
        lock (_gate) { _tokenText = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)); }
        PublishDescriptor();
    }

    public string TokenForTest => _tokenText;

    /// <summary>Hane の再起動: 新しい instanceId とトークン、同じ文書の ID は変わる。進行中の接続は切れる。</summary>
    public void Restart(string newDocumentId)
    {
        lock (_gate)
        {
            InstanceId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
            _tokenText = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            DocumentId = newDocumentId;
            _handles.Clear();
            _ops.Clear();
        }
        // パイプ名は instanceId を含むため、新しいサーバーを立て直す代わりに同じパイプ名で受ける（名前は記述子で公開する）。
        PipeName = $"Hane.Whisparrot.{Pid}.{InstanceId}";
        _restartPending = true;
        PublishDescriptor();
        foreach (var p in _live.ToArray()) { try { p.Dispose(); } catch { } }
    }

    private volatile bool _restartPending;

    // ── サーバー本体 ──

    private async Task AcceptLoop()
    {
        var name = PipeName;
        while (!_stop.IsCancellationRequested)
        {
            if (_restartPending) { name = PipeName; _restartPending = false; }
            var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                var waiting = pipe.WaitForConnectionAsync(wait.Token);
                // 再起動の通知を待つ間も受付を続ける（名前の切替のため定期的に起こす）。
                while (!waiting.IsCompleted)
                {
                    await Task.WhenAny(waiting, Task.Delay(20)).ConfigureAwait(false);
                    if (_restartPending || _stop.IsCancellationRequested) { wait.Cancel(); break; }
                }
                if (!waiting.IsCompletedSuccessfully) { pipe.Dispose(); continue; }
            }
            catch { pipe.Dispose(); continue; }
            lock (_live) _live.Add(pipe);
            _ = Task.Run(() => Serve(pipe));
        }
    }

    private async Task Serve(NamedPipeServerStream pipe)
    {
        var authed = false;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var req = await ReadAsync(pipe).ConfigureAwait(false);
                var id = req["id"]!.DeepClone();
                var method = req["method"]!.GetValue<string>();
                var p = req["params"] as JsonObject ?? new JsonObject();
                lock (_gate) _frames.Add(new Frame(method, p["operationId"]?.GetValue<string>(), Redact(p)));

                if (method == "hello")
                {
                    JsonNode reply;
                    Hellos++;
                    var presented = p["token"]?.GetValue<string>() ?? "";
                    string? expected; lock (_gate) expected = _tokenText;
                    var ok = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(expected));
                    if (!ok) { await WriteAsync(pipe, Error(id, "AUTH_FAILED", "not_applied")).ConfigureAwait(false); break; }
                    var cmin = p["protocol"]!["min"]!["major"]!.GetValue<int>();
                    var cmax = p["protocol"]!["max"]!["major"]!.GetValue<int>();
                    var agreed = Math.Min(cmax, ServerMajor.Max);
                    if (agreed < Math.Max(cmin, ServerMajor.Min)) { await WriteAsync(pipe, Error(id, "UNSUPPORTED_VERSION", "not_applied")).ConfigureAwait(false); break; }
                    authed = true;
                    reply = new JsonObject
                    {
                        ["id"] = id,
                        ["result"] = new JsonObject
                        {
                            ["protocol"] = new JsonObject { ["major"] = agreed, ["minor"] = 0 },
                            ["instanceId"] = InstanceId,
                            ["features"] = new JsonArray("context.get", "edit.insert", "edit.replace"),
                            ["futureField"] = new JsonObject { ["ignored"] = true },
                        },
                    };
                    await WriteAsync(pipe, reply).ConfigureAwait(false);
                    continue;
                }
                if (!authed) break; // 認証前に文書情報を返さない

                var response = await Dispatch(method, p, id).ConfigureAwait(false);
                if (response is null) break; // 応答せず切断（応答喪失）
                await WriteAsync(pipe, response).ConfigureAwait(false);
            }
        }
        catch { /* 切断 */ }
        finally { lock (_live) _live.Remove(pipe); try { pipe.Dispose(); } catch { } }
    }

    private static JsonObject Redact(JsonObject p)
    {
        var c = (JsonObject)p.DeepClone();
        c.Remove("token");
        return c;
    }

    private static JsonObject Error(JsonNode id, string code, string effect) =>
        new() { ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["effect"] = effect } };

    private async Task<JsonNode?> Dispatch(string method, JsonObject p, JsonNode id)
    {
        if (method == "context.get") return Context(p, id);
        if (method is not ("edit.insert" or "edit.replace")) return Error(id, "INVALID_REQUEST", "not_applied");

        var opId = p["operationId"]?.GetValue<string>() ?? "";
        var fingerprint = Redact(p).ToJsonString();
        TaskCompletionSource? hold = null;
        lock (_gate)
        {
            Mutations++;
            if (_ops.TryGetValue(opId, out var prior))
            {
                if (prior.Fingerprint != fingerprint) return Error(id, "OPERATION_CONFLICT", "not_applied");
                if (!prior.Done) return Error(id, "IN_PROGRESS", "unknown");
                if (DropEveryConnectionOnMutation) return null;
                return Wrap(id, prior.Response);
            }
            if (DropEveryConnectionOnMutation) return null;
            if (DropRequestBeforeApply) { DropRequestBeforeApply = false; return null; }
            if (long.Parse(p["deadlineTick"]!.GetValue<string>()) < Environment.TickCount64) return Error(id, "DEADLINE_EXCEEDED", "not_applied");
            if (NextMutationError is { } code) { NextMutationError = null; return Error(id, code, "not_applied"); }
            _ops[opId] = (fingerprint, null, false);
            hold = HoldApply;
        }
        if (hold is not null) { HoldApply = null; await hold.Task.ConfigureAwait(false); }

        JsonNode result;
        lock (_gate)
        {
            var (ok, body) = method == "edit.insert" ? Insert(p) : Replace(p);
            if (!ok)
            {
                _ops.Remove(opId); // 拒否は記録しない（同じ ID で再度試せる）
                return Error(id, ((JsonObject)body)["code"]!.GetValue<string>(), "not_applied");
            }
            result = body;
            _ops[opId] = (fingerprint, result, true);
            if (DropResponseAfterApply) { DropResponseAfterApply = false; return null; }
        }
        return Wrap(id, result);
    }

    private static JsonNode Wrap(JsonNode id, JsonNode? result) => new JsonObject { ["id"] = id.DeepClone(), ["result"] = result?.DeepClone() };

    private JsonNode Context(JsonObject p, JsonNode id)
    {
        lock (_gate)
        {
            if (ContextError is { } e) return Error(id, e, "not_applied");
            if (p["hwnd"]!.GetValue<long>() != Hwnd) return Error(id, "STALE_TARGET", "not_applied");
            var beforeMax = p["beforeChars"]!.GetValue<int>();
            var afterMax = p["afterChars"]!.GetValue<int>();
            var cursor = (int)Active;
            var before = Encoding.UTF8.GetString(_doc, 0, cursor);
            var after = Encoding.UTF8.GetString(_doc, cursor, _doc.Length - cursor);
            var (b, bStart) = TakeLastScalars(before, beforeMax);
            var (a, _) = (TakeFirstScalars(after, afterMax), 0);
            var pending = new JsonArray();
            foreach (var h in (p["pendingRangeHandles"] as JsonArray ?? []).Select(x => x!.GetValue<string>()))
                pending.Add(_handles.TryGetValue(h, out var hh)
                    ? new JsonObject { ["rangeHandle"] = h, ["tracked"] = true, ["startByte"] = hh.Start, ["endByte"] = hh.End }
                    : new JsonObject { ["rangeHandle"] = h, ["tracked"] = false });
            return new JsonObject
            {
                ["id"] = id,
                ["result"] = new JsonObject
                {
                    ["documentId"] = DocumentId,
                    ["revision"] = Revision.ToString(),
                    ["cursorByte"] = Active,
                    ["selection"] = new JsonObject { ["anchorByte"] = Anchor, ["activeByte"] = Active },
                    ["title"] = "mock.md",
                    ["isUntitled"] = false,
                    ["frontMatter"] = new JsonObject { ["status"] = "absent" },
                    ["before"] = b,
                    ["after"] = a,
                    ["beforeTruncated"] = b.Length < before.Length,
                    ["afterTruncated"] = a.Length < after.Length,
                    ["beforeStartByte"] = Encoding.UTF8.GetByteCount(before[..(before.Length - b.Length)]),
                    ["afterStartByte"] = Active,
                    ["pendingRanges"] = pending,
                    ["futureField"] = 1,
                },
            };
        }
    }

    private static (string Text, int Start) TakeLastScalars(string s, int max)
    {
        var runes = s.EnumerateRunes().ToArray();
        var take = runes.Skip(Math.Max(0, runes.Length - max));
        return (string.Concat(take.Select(r => r.ToString())), 0);
    }

    private static string TakeFirstScalars(string s, int max) => string.Concat(s.EnumerateRunes().Take(max).Select(r => r.ToString()));

    private (bool Ok, JsonNode Body) Insert(JsonObject p)
    {
        if (p["hwnd"]!.GetValue<long>() != Hwnd || p["documentId"]!.GetValue<string>() != DocumentId) return Rej("STALE_TARGET");
        if (ContextError is { } e) return Rej(e);
        var text = Encoding.UTF8.GetBytes(p["text"]!.GetValue<string>());
        if (text.Length > 32 * 1024) return Rej("INVALID_REQUEST");
        if (p["expectedRevision"]!.GetValue<string>() != Revision.ToString()) return Rej("STALE_VERSION");
        var sel = p["expectedSelection"]!;
        if (sel["anchorByte"]!.GetValue<long>() != Anchor || sel["activeByte"]!.GetValue<long>() != Active) return Rej("STALE_CURSOR");
        var start = Active; // 選択中の文字は消さず、アクティブ端に挿入する
        ApplyEdit(start, 0, text);
        Anchor = Active = start + text.Length; // 成功後のキャレットは挿入末尾
        var hash = Convert.ToHexStringLower(SHA256.HashData(text));
        var handle = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        _handles[handle] = new Handle(DocumentId, Revision, start, start + text.Length, text, hash, Environment.TickCount64 + 120_000);
        return (true, new JsonObject
        {
            ["documentId"] = DocumentId,
            ["insertedRevision"] = Revision.ToString(),
            ["range"] = new JsonObject { ["startByte"] = start, ["endByte"] = start + text.Length },
            ["rangeHandle"] = handle,
            ["expectedTextHash"] = hash,
            ["expiresInMs"] = 120_000,
        });
    }

    private (bool Ok, JsonNode Body) Replace(JsonObject p)
    {
        var handle = p["rangeHandle"]!.GetValue<string>();
        if (!_handles.TryGetValue(handle, out var h) || h.ExpiresAt < Environment.TickCount64) return Rej("RANGE_EXPIRED");
        if (p["hwnd"]!.GetValue<long>() != Hwnd || h.DocumentId != DocumentId || p["documentId"]!.GetValue<string>() != DocumentId) return Rej("STALE_TARGET");
        if (p["insertedRevision"]!.GetValue<string>() != h.Revision.ToString() || p["expectedTextHash"]!.GetValue<string>() != h.Hash) return Rej("RANGE_CHANGED");

        // 挿入版から後続の編集を順に適用して範囲を追跡する。内部・重なりは RANGE_CHANGED、先頭への挿入は右へ移し、末尾への挿入は範囲外。
        long start = h.Start, end = h.End;
        foreach (var (rev, es, del, ins) in _log.Where(l => l.Rev > h.Revision))
        {
            var ee = es + del;
            if (del == 0 && es == start) { start += ins; end += ins; }           // 範囲先頭への挿入は範囲を右へ移す
            else if (ee <= start) { start += ins - del; end += ins - del; }      // 範囲より前の編集
            else if (es >= end) { /* 範囲外（末尾への挿入を含む） */ }
            else return Rej("RANGE_CHANGED");                                    // 内部・重なり
        }
        // 再配置後の内容は、保持した原文と完全一致しなければならない。
        if (end > _doc.Length || !_doc.AsSpan((int)start, (int)(end - start)).SequenceEqual(h.Text)) return Rej("RANGE_CHANGED");

        var repl = Encoding.UTF8.GetBytes(p["replacement"]!.GetValue<string>());
        var caretWasAtEnd = Active == end;
        ApplyEdit(start, end - start, repl);
        if (caretWasAtEnd) Anchor = Active = start + repl.Length;
        _handles.Remove(handle); // 消費
        return (true, new JsonObject
        {
            ["documentId"] = DocumentId,
            ["revision"] = Revision.ToString(),
            ["range"] = new JsonObject { ["startByte"] = start, ["endByte"] = start + repl.Length },
            ["undoMode"] = "merged",
        });
    }

    private static (bool, JsonNode) Rej(string code) => (false, new JsonObject { ["code"] = code });

    // ── ワイヤ ──

    private static async Task<JsonObject> ReadAsync(Stream s)
    {
        var h = new byte[4];
        await s.ReadExactlyAsync(h).ConfigureAwait(false);
        var len = BinaryPrimitives.ReadUInt32LittleEndian(h);
        if (len is 0 or > 256 * 1024) throw new IOException("frame size");
        var b = new byte[len];
        await s.ReadExactlyAsync(b).ConfigureAwait(false);
        return (JsonNode.Parse(b) as JsonObject)!;
    }

    private static async Task WriteAsync(Stream s, JsonNode frame)
    {
        var json = Encoding.UTF8.GetBytes(frame.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        var buf = new byte[4 + json.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)json.Length);
        json.CopyTo(buf, 4);
        await s.WriteAsync(buf).ConfigureAwait(false);
        await s.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>テスト用: サーバーを止める（以後の接続は失敗する）。記述子は残す。</summary>
    public void Kill()
    {
        _stop.Cancel();
        foreach (var p in _live.ToArray()) { try { p.Dispose(); } catch { } }
    }

    public void Dispose()
    {
        Kill();
        try { _accept.Wait(1000); } catch { }
        try { Directory.Delete(Root, recursive: true); } catch { }
    }
}
