using System.IO.Pipes;
using Whisper.net;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;

namespace Whisparrot.Infrastructure.Recognition;

/// <summary>
/// 同じ exe の <c>--recognizer-worker</c> モード（子側）。
/// 親が作ったパイプへ接続し、nonce で名乗り、モデルをロードして Ready を返し、Request ごとに認識する。
/// 親が閉じる（EOF）か Shutdown を受けたら終了する。ネイティブ処理は中断できないため、取消は親が子を終了する。
/// </summary>
public static class WorkerHost
{
    public const string Flag = "--recognizer-worker";

    public static int Run(string[] args, INativeResourceSource resources, string version)
    {
        try
        {
            var opts = WorkerArgs.Parse(args);
            return RunAsync(opts, resources, version).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"worker error: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunAsync(WorkerArgs opts, INativeResourceSource resources, string version)
    {
        var native = new NativeRuntime(resources, AppPaths.RuntimeCacheRoot, version, NativeRuntime.CurrentRid());
        var logs = new List<string>();
        var (backendLabel, libraryName) = ConfigureNative(native, opts.Backend, logs);

        await using var pipe = new NamedPipeClientStream(".", opts.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(10_000).ConfigureAwait(false);
        await WorkerProtocol.WriteAsync(pipe, FrameType.Hello, Convert.FromHexString(opts.Nonce), CancellationToken.None).ConfigureAwait(false);

        WhisperFactory factory;
        try
        {
            factory = WhisperFactory.FromPath(opts.ModelPath);
        }
        catch (Exception ex)
        {
            await WorkerProtocol.WriteJsonAsync(pipe, FrameType.Error, new ErrorInfo($"モデルを読み込めません: {ex.Message}"), CancellationToken.None).ConfigureAwait(false);
            return 2;
        }

        using (factory)
        {
            var effective = DetectBackend(backendLabel, logs);
            await WorkerProtocol.WriteJsonAsync(pipe, FrameType.Ready,
                new ReadyInfo(effective, libraryName, string.Join(" | ", logs.TakeLast(6))), CancellationToken.None).ConfigureAwait(false);

            while (true)
            {
                (FrameType Type, byte[] Payload) frame;
                try
                {
                    frame = await WorkerProtocol.ReadAsync(pipe, CancellationToken.None).ConfigureAwait(false);
                }
                catch (EndOfStreamException)
                {
                    return 0; // 親が閉じた
                }
                if (frame.Type == FrameType.Shutdown) return 0;
                if (frame.Type != FrameType.Request) throw new InvalidDataException($"想定外のフレーム: {frame.Type}");

                try
                {
                    var (header, samples) = WorkerProtocol.ParseRequest(frame.Payload);
                    var text = await RecognizeAsync(factory, header, samples).ConfigureAwait(false);
                    await WorkerProtocol.WriteJsonAsync(pipe, FrameType.Result, new ResultInfo(text, effective), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not InvalidDataException)
                {
                    await WorkerProtocol.WriteJsonAsync(pipe, FrameType.Error, new ErrorInfo($"認識に失敗しました: {ex.Message}"), CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task<string> RecognizeAsync(WhisperFactory factory, RequestHeader header, float[] samples)
    {
        using var processor = factory.CreateBuilder()
            .WithLanguage(header.Language)
            .WithThreads(header.Threads)
            .Build();
        var text = new System.Text.StringBuilder();
        await foreach (var seg in processor.ProcessAsync(samples))
        {
            text.Append(seg.Text);
        }
        return text.ToString().Trim();
    }

    /// <summary>
    /// 埋め込み native を展開し、Whisper.net が展開先の whisper.dll を使うよう設定する。
    /// 要求バックエンドが埋め込まれていなければ CPU（要求が cpu のときは当然 CPU）。
    /// </summary>
    private static (string BackendLabel, string Library) ConfigureNative(NativeRuntime native, string backend, List<string> logs)
    {
        native.Extract();
        native.PreloadVcRuntime();
        var rel = native.WhisperDllRelative(backend);
        var label = backend;
        if (rel is null)
        {
            rel = native.WhisperDllRelative("cpu") ?? throw new FileNotFoundException("埋め込み native runtime が見つかりません");
            label = "cpu";
        }
        RuntimeOptions.LibraryPath = Path.Combine(native.Directory, rel.Replace('/', Path.DirectorySeparatorChar));
        LogProvider.AddLogger((_, msg) =>
        {
            if (string.IsNullOrWhiteSpace(msg)) return;
            lock (logs) { if (logs.Count < 200) logs.Add(msg.TrimEnd()); }
        });
        return (label, label);
    }

    /// <summary>
    /// 「ライブラリがロードできた」と「実際に GPU で計算する」を区別する（スパイク §3.2）。
    /// Vulkan 要求でもログに "no GPU found" があれば CPU で動いている。
    /// </summary>
    internal static string DetectBackend(string requested, IReadOnlyList<string> logs)
    {
        if (requested != "vulkan") return "cpu";
        string[] snapshot;
        lock (logs) snapshot = logs.ToArray();
        if (snapshot.Any(l => l.Contains("no GPU found", StringComparison.OrdinalIgnoreCase))) return "cpu";
        return snapshot.Any(l => l.Contains("Vulkan", StringComparison.OrdinalIgnoreCase) && l.Contains("device", StringComparison.OrdinalIgnoreCase))
            ? "vulkan"
            : "cpu";
    }
}

internal sealed record WorkerArgs(string PipeName, string Nonce, string Backend, string ModelPath)
{
    public static WorkerArgs Parse(string[] args)
    {
        string? Opt(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        return new WorkerArgs(
            Opt("--pipe") ?? throw new ArgumentException("--pipe が必要です"),
            Opt("--nonce") ?? throw new ArgumentException("--nonce が必要です"),
            Opt("--backend") ?? "cpu",
            Opt("--model") ?? throw new ArgumentException("--model が必要です"));
    }
}
