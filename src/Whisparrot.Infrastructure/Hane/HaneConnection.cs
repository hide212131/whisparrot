using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json.Nodes;
using Whisparrot.Core.Hane;

namespace Whisparrot.Infrastructure.Hane;

internal sealed class HaneClientOptions
{
    /// <summary>記述子の置き場。既定は <c>%LOCALAPPDATA%/Hane/integration/whisparrot</c>。</summary>
    public string DescriptorDirectory { get; init; } = Path.Combine(DefaultRoot(), "Hane", "integration", "whisparrot");

    /// <summary>ここから記述子のディレクトリまでの再解析ポイントを拒否する。</summary>
    public string TrustedRoot { get; init; } = DefaultRoot();

    /// <summary>挿入・差し替えの初回の期限（設計 §4.5: 250 ms）。</summary>
    public TimeSpan MutationTimeout { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>応答喪失後の照会に追加する時間（設計 §4.5: 500 ms）。</summary>
    public TimeSpan RetryBudget { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary><c>IN_PROGRESS</c> の間に再度照会する間隔。</summary>
    public TimeSpan InProgressPoll { get; init; } = TimeSpan.FromMilliseconds(40);

    /// <summary>Windows 起動後の単調時計（ミリ秒。<c>GetTickCount64</c>）。Hane と共通の期限の基準（設計 §3.4）。</summary>
    public Func<long> TickMs { get; init; } = static () => Environment.TickCount64;

    /// <summary>この版が話せるプロトコルの範囲。major 不一致は連携不可（設計 §2）。</summary>
    public HaneVersion ClientMin { get; init; } = new(1, 0);

    public HaneVersion ClientMax { get; init; } = new(1, 0);

    private static string DefaultRoot() => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
}

/// <summary>接続・認証の失敗など、要求の送信前後を問わず分類できる失敗。</summary>
internal sealed class HaneIoException(HaneFailure failure) : Exception(failure.ToString())
{
    public HaneFailure Failure { get; } = failure;
}

/// <summary>
/// 認証済みの 1 本のパイプ接続。同時に処理する要求は一件（設計 §2）。記述子の読取 → サーバー PID の照合 → <c>hello</c>（トークン・版範囲）。
/// 照合に失敗した相手へはトークンを送らない。クライアントは偽装権限を渡さない。
/// </summary>
internal sealed class HaneConnection : IDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private long _nextId;

    private HaneConnection(NamedPipeClientStream pipe) => _pipe = pipe;

    public HaneHello Hello { get; private set; } = new(new HaneVersion(0, 0), "", []);

    public static async Task<(HaneConnection? Connection, HaneFailure Failure)> OpenAsync(HaneWindow window, HaneClientOptions o, CancellationToken ct)
    {
        var (desc, failure) = HaneDescriptorReader.Read(o.DescriptorDirectory, o.TrustedRoot, window.Pid);
        if (desc is null) return (null, failure);
        // 版範囲が重ならなければ、トークンを送る前に連携不可とする。
        if (desc.MaxVersion.Major < o.ClientMin.Major || desc.MinVersion.Major > o.ClientMax.Major) return (null, HaneFailure.UnsupportedVersion);

        // クライアントは偽装権限を渡さない（Anonymous）。
        var pipe = new NamedPipeClientStream(".", desc.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous);
        try
        {
            try { await pipe.ConnectAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException) { pipe.Dispose(); return (null, HaneFailure.ConnectFailed); }

            if (HaneNative.ServerProcessId(pipe.SafePipeHandle) != desc.Pid) { pipe.Dispose(); return (null, HaneFailure.ServerMismatch); }

            var conn = new HaneConnection(pipe);
            var (hello, helloFailure) = await conn.HelloAsync(desc, o, ct).ConfigureAwait(false);
            if (hello is null) { pipe.Dispose(); return (null, helloFailure); }
            conn.Hello = hello;
            return (conn, HaneFailure.None);
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    private async Task<(HaneHello? Value, HaneFailure Failure)> HelloAsync(HaneDescriptor desc, HaneClientOptions o, CancellationToken ct)
    {
        var args = new JsonObject
        {
            ["token"] = desc.Token,
            ["client"] = "whisparrot",
            ["protocol"] = new JsonObject { ["min"] = Version(o.ClientMin), ["max"] = Version(o.ClientMax) },
        };
        JsonObject resp;
        try { resp = await RequestAsync("hello", args, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or HaneWire.FrameException or HaneIoException or ObjectDisposedException)
        {
            return (null, ex is HaneWire.FrameException { TooLarge: true } ? HaneFailure.TooLarge : HaneFailure.AuthRejected); // 認証拒否は接続を閉じられる（設計 §2）
        }
        if (resp["error"] is JsonObject err)
            return (null, HaneJson.SafeCode(err) == "UNSUPPORTED_VERSION" ? HaneFailure.UnsupportedVersion : HaneFailure.AuthRejected);
        if (resp["result"] is not JsonObject r) return (null, HaneFailure.Protocol);

        var agreed = HaneJson.Version(r, "protocol") ?? HaneJson.Version(r, "version");
        var iid = HaneJson.Str(r, "instanceId");
        if (agreed is null || iid is null) return (null, HaneFailure.Protocol);
        if (agreed.Major < o.ClientMin.Major || agreed.Major > o.ClientMax.Major || agreed.Major < desc.MinVersion.Major || agreed.Major > desc.MaxVersion.Major)
            return (null, HaneFailure.UnsupportedVersion);
        if (iid != desc.InstanceId) return (null, HaneFailure.ServerMismatch);

        var features = r["features"] is JsonArray fa
            ? fa.Select(f => f is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().Take(32).ToArray()
            : [];
        return (new HaneHello(agreed, iid, features), HaneFailure.None);
    }

    /// <summary>要求を送って対応する応答のフレームを返す。<c>id</c> が合わない応答は <see cref="HaneIoException"/>（Protocol）。</summary>
    public async Task<JsonObject> RequestAsync(string method, JsonObject args, CancellationToken ct)
    {
        var id = ++_nextId;
        await HaneWire.WriteAsync(_pipe, new JsonObject { ["id"] = id, ["method"] = method, ["params"] = args.DeepClone() }, ct).ConfigureAwait(false);
        var resp = await HaneWire.ReadAsync(_pipe, ct).ConfigureAwait(false);
        if (resp["id"] is not JsonValue v || !v.TryGetValue<long>(out var rid) || rid != id) throw new HaneIoException(HaneFailure.Protocol);
        return resp;
    }

    private static JsonObject Version(HaneVersion v) => new() { ["major"] = v.Major, ["minor"] = v.Minor };

    public void Dispose() => _pipe.Dispose();
}
