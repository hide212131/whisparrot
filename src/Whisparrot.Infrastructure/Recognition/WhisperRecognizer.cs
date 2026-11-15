using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Whisparrot.Core;

namespace Whisparrot.Infrastructure.Recognition;

public sealed record WhisperRecognizerOptions(
    string ModelPath,
    /// <summary>試行順。実際に使えるのは native が埋め込まれているものだけ。最後は常に cpu。</summary>
    IReadOnlyList<string> BackendOrder,
    IReadOnlySet<string> AvailableBackends,
    /// <summary>モデルの想定サイズ。T09 までは存在とサイズの確認だけ（ハッシュ照合は T09）。0 なら確認しない。</summary>
    long ExpectedModelBytes = 0,
    TimeSpan? StartupTimeout = null,
    TimeSpan? RecognizeTimeout = null);

/// <summary>
/// 親側の認識。認識は必ず子プロセス（同じ exe の worker）で行い、PCM は親が保持する。
/// GPU 子の初期化失敗・異常終了・プロトコル不正は、子を変えて CPU で一度だけ再試行する。
/// 取消（<see cref="CancellationToken"/>）は子の終了で実現し、孤児を残さない。
/// </summary>
public sealed class WhisperRecognizer : IRecognizer
{
    /// <summary>ggml-small.bin（Hugging Face ggerganov/whisper.cpp）のバイト数。</summary>
    public const long SmallModelBytes = 487_601_967;

    private readonly WhisperRecognizerOptions _options;
    private readonly IWorkerLauncher _launcher;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _disabledBackends = [];
    private Worker? _worker;
    private bool _disposed;

    public WhisperRecognizer(WhisperRecognizerOptions options) : this(options, new ProcessWorkerLauncher()) { }

    internal WhisperRecognizer(WhisperRecognizerOptions options, IWorkerLauncher launcher)
    {
        _options = options;
        _launcher = launcher;
    }

    /// <summary>直近の認識子が報告した実行方式（未実行なら null）。</summary>
    public string? LastBackend { get; private set; }

    /// <summary>直近の認識子が報告した診断（ロードしたライブラリ名と whisper.cpp ログの末尾）。</summary>
    public string? LastWorkerDetail { get; private set; }

    public string? CheckReady()
    {
        var path = _options.ModelPath;
        if (!File.Exists(path)) return $"モデルがありません: {path}";
        var len = new FileInfo(path).Length;
        if (_options.ExpectedModelBytes > 0 && len != _options.ExpectedModelBytes)
            return $"モデルのサイズが想定と違います（{len} bytes）";
        return null;
    }

    public async Task<RecognitionResult> RecognizeAsync(RecognitionRequest request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var plan = BuildPlan();
            for (var i = 0; i < plan.Count; i++)
            {
                var backend = plan[i];
                try
                {
                    var result = await RunOnceAsync(backend, request, ct).ConfigureAwait(false);
                    LastBackend = result.Backend;
                    LastWorkerDetail = _worker is { } w ? $"library={w.Ready.Library} log={w.Ready.Detail}" : null;
                    return result;
                }
                catch (OperationCanceledException)
                {
                    KillWorker(); // 取消: ネイティブ処理は中断できないので子ごと終了する
                    throw;
                }
                catch (Exception ex)
                {
                    KillWorker();
                    var isLast = i == plan.Count - 1;
                    if (backend != "cpu") _disabledBackends.Add(backend); // このプロセスの間は GPU を再試行しない
                    if (isLast) throw ex as RecognitionException ?? new RecognitionException($"認識に失敗しました（{backend}）: {ex.Message}", ex);
                    // 親が持つ同じ PCM を、子を変えて CPU で一度だけ再試行する。
                }
            }
            throw new RecognitionException("認識方式がありません");
        }
        finally
        {
            _gate.Release();
        }
    }

    private List<string> BuildPlan()
    {
        var plan = new List<string>();
        foreach (var b in _options.BackendOrder)
        {
            if (b == "cpu") continue;
            if (_options.AvailableBackends.Contains(b) && !_disabledBackends.Contains(b) && !plan.Contains(b)) plan.Add(b);
        }
        // GPU は最初の一つだけ試し、その後に CPU（再試行は一度だけ）。
        if (plan.Count > 1) plan.RemoveRange(1, plan.Count - 1);
        plan.Add("cpu");
        return plan;
    }

    private async Task<RecognitionResult> RunOnceAsync(string backend, RecognitionRequest request, CancellationToken ct)
    {
        var worker = _worker;
        if (worker is null || worker.Backend != backend || worker.Process.HasExited)
        {
            KillWorker();
            worker = _worker = await StartWorkerAsync(backend, ct).ConfigureAwait(false);
        }

        var threads = Math.Max(1, Math.Min(8, Environment.ProcessorCount - 1));
        var payload = WorkerProtocol.BuildRequest(new RequestHeader(request.Language, threads, request.Pcm.Samples.Length), request.Pcm.Samples);
        using var timeout = new CancellationTokenSource(_options.RecognizeTimeout ?? TimeSpan.FromMinutes(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        // 取消されたら子を終了する。ReadAsync が EOF/例外で抜ける前に OperationCanceledException に揃える。
        using var reg = linked.Token.Register(worker.Process.Kill);
        try
        {
            await WorkerProtocol.WriteAsync(worker.Pipe, FrameType.Request, payload, linked.Token).ConfigureAwait(false);
            var (type, body) = await WorkerProtocol.ReadAsync(worker.Pipe, linked.Token).ConfigureAwait(false);
            return type switch
            {
                FrameType.Result => ToResult(WorkerProtocol.ParseJson<ResultInfo>(body)),
                FrameType.Error => throw new RecognitionException(WorkerProtocol.ParseJson<ErrorInfo>(body).Message),
                _ => throw new InvalidDataException($"想定外のフレーム: {type}"),
            };
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
        catch (Exception) when (timeout.IsCancellationRequested)
        {
            throw new RecognitionException("認識がタイムアウトしました");
        }
    }

    private static RecognitionResult ToResult(ResultInfo r) => new(r.Text, r.Backend);

    private async Task<Worker> StartWorkerAsync(string backend, CancellationToken ct)
    {
        var pipeName = "whisparrot-" + Guid.NewGuid().ToString("N");
        var nonceBytes = RandomNumberGenerator.GetBytes(16);
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, 0, security);
        IWorkerProcess? process = null;
        try
        {
            process = _launcher.Launch(new WorkerStartInfo(backend, pipeName, Convert.ToHexString(nonceBytes), _options.ModelPath));
            var startup = _options.StartupTimeout ?? TimeSpan.FromSeconds(120);
            using var timeout = new CancellationTokenSource(startup);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            // 子が接続前に死んだ場合に待ち続けない。
            using var exitWatch = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            var exited = process.WaitForExitAsync(exitWatch.Token).ContinueWith(_ => { if (!exitWatch.IsCancellationRequested) linked.Cancel(); }, TaskScheduler.Default);
            try
            {
                await pipe.WaitForConnectionAsync(linked.Token).ConfigureAwait(false);
                var (type, body) = await WorkerProtocol.ReadAsync(pipe, linked.Token).ConfigureAwait(false);
                if (type != FrameType.Hello || !CryptographicOperations.FixedTimeEquals(body, nonceBytes))
                    throw new InvalidDataException("認識子の nonce が一致しません");
                var (rtype, rbody) = await WorkerProtocol.ReadAsync(pipe, linked.Token).ConfigureAwait(false);
                if (rtype == FrameType.Error) throw new RecognitionException(WorkerProtocol.ParseJson<ErrorInfo>(rbody).Message);
                if (rtype != FrameType.Ready) throw new InvalidDataException($"想定外のフレーム: {rtype}");
                var ready = WorkerProtocol.ParseJson<ReadyInfo>(rbody);
                exitWatch.Cancel();
                return new Worker(backend, process, pipe, ready);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new RecognitionException(timeout.IsCancellationRequested ? "認識子の起動がタイムアウトしました" : $"認識子が起動できませんでした（終了コード {process.ExitCode}）");
            }
            catch (EndOfStreamException)
            {
                throw new RecognitionException($"認識子が異常終了しました（終了コード {process.ExitCode}）");
            }
            finally
            {
                exitWatch.Cancel();
                try { await exited.ConfigureAwait(false); } catch { }
            }
        }
        catch
        {
            try { process?.Kill(); } catch { }
            process?.Dispose();
            pipe.Dispose();
            throw;
        }
    }

    private void KillWorker()
    {
        var w = _worker;
        _worker = null;
        w?.Dispose();
    }

    public void Dispose()
    {
        _disposed = true;
        KillWorker();
    }

    private sealed class Worker(string backend, IWorkerProcess process, NamedPipeServerStream pipe, ReadyInfo ready) : IDisposable
    {
        public string Backend { get; } = backend;
        public IWorkerProcess Process { get; } = process;
        public NamedPipeServerStream Pipe { get; } = pipe;
        public ReadyInfo Ready { get; } = ready;

        public void Dispose()
        {
            try { Process.Kill(); } catch { }
            Pipe.Dispose();
            Process.Dispose();
        }
    }
}
