using System.Diagnostics;
using System.Security.Cryptography;
using Whisparrot.Infrastructure.Network;

namespace Whisparrot.Infrastructure.Models;

public enum ModelState { NotDownloaded, Ready, Corrupt, Downloading }

public sealed record ModelStatus(string Id, ModelState State, long SizeBytes, string? Detail = null);

public sealed record DownloadProgress(string Id, long BytesDone, long BytesTotal);

public class ModelException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>別のモデル（または同じモデル）を取得中。</summary>
public sealed class ModelBusyException(string message) : ModelException(message);

/// <summary>読み込み中のモデルは置き換えない。</summary>
public sealed class ModelInUseException(string message) : ModelException(message);

/// <summary>
/// 設計 §4.2: 一時ファイルへ取得 → サイズと SHA-256 を照合 → 同じボリュームで rename して公開する。
/// 失敗・取消・不一致では正式ファイルに触れない。取得は必ず <see cref="NetworkContext"/> 経由。
/// </summary>
public sealed class ModelManager
{
    private const int BufferSize = 256 * 1024;
    private const long FreeSpaceMargin = 64L * 1024 * 1024;

    private readonly string _dir;
    private readonly ModelCatalog _catalog;
    private readonly NetworkContext _network;
    private readonly Func<string, bool> _isInUse;
    private readonly Func<long>? _freeSpace;
    private readonly object _lock = new();
    private readonly Dictionary<string, (long Length, long MtimeTicks, bool Ok)> _verified = [];
    private string? _downloadingId;

    public ModelManager(string modelsDirectory, ModelCatalog catalog, NetworkContext network, Func<string, bool>? isInUse = null, Func<long>? freeSpaceBytes = null)
    {
        _dir = modelsDirectory;
        _catalog = catalog;
        _network = network;
        _isInUse = isInUse ?? (_ => false);
        _freeSpace = freeSpaceBytes;
    }

    public ModelCatalog Catalog => _catalog;

    public string PathOf(string id) => Path.Combine(_dir, _catalog.Get(id).Filename);

    private string PartPathOf(string id) => PathOf(id) + ".part";

    /// <summary>サイズと更新時刻をキーに検証結果をキャッシュする。変更が見つかれば再検証する。</summary>
    public async Task<ModelStatus> GetStatusAsync(string id, CancellationToken ct = default)
    {
        var e = _catalog.Get(id);
        lock (_lock) if (_downloadingId == id) return new(id, ModelState.Downloading, e.SizeBytes);
        return await VerifyAsync(e, ct).ConfigureAwait(false);
    }

    private async Task<ModelStatus> VerifyAsync(ModelEntry e, CancellationToken ct)
    {
        var id = e.Id;
        var path = PathOf(id);
        var fi = new FileInfo(path);
        if (!fi.Exists) return new(id, ModelState.NotDownloaded, e.SizeBytes);
        if (fi.Length != e.SizeBytes) return new(id, ModelState.Corrupt, e.SizeBytes, $"サイズが違います（{fi.Length} / {e.SizeBytes} bytes）");

        lock (_lock)
            if (_verified.TryGetValue(id, out var c) && c.Length == fi.Length && c.MtimeTicks == fi.LastWriteTimeUtc.Ticks)
                return c.Ok ? new(id, ModelState.Ready, e.SizeBytes) : new(id, ModelState.Corrupt, e.SizeBytes, "SHA-256 が一致しません");

        var hash = await HashFileAsync(path, ct).ConfigureAwait(false);
        var ok = string.Equals(hash, e.Sha256, StringComparison.Ordinal);
        fi.Refresh();
        lock (_lock) _verified[id] = (fi.Length, fi.LastWriteTimeUtc.Ticks, ok);
        return ok ? new(id, ModelState.Ready, e.SizeBytes) : new(id, ModelState.Corrupt, e.SizeBytes, "SHA-256 が一致しません");
    }

    /// <summary>認識に使えるなら null、使えないなら利用者向けの理由。未検証なら同期的に検証する（初回のみ時間がかかる）。</summary>
    public string? CheckReady(string id)
    {
        ModelStatus st;
        try { st = GetStatusAsync(id).GetAwaiter().GetResult(); }
        catch (Exception ex) { return $"モデルを検証できません: {ex.Message}"; }
        return st.State switch
        {
            ModelState.Ready => null,
            ModelState.NotDownloaded => $"モデル「{id}」が未取得です。設定でモデルを取得してください",
            ModelState.Downloading => $"モデル「{id}」を取得中です",
            _ => $"モデル「{id}」が壊れています（{st.Detail}）。設定で再取得してください",
        };
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false)).ToLowerInvariant();
    }

    /// <summary>
    /// 取得する。既に検証済みのモデルがあれば何もしない（既存は保持）。壊れた既存は、検証済みの新ファイルができるまで残す。
    /// </summary>
    public async Task DownloadAsync(string id, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var e = _catalog.Get(id);
        lock (_lock)
        {
            if (_downloadingId is not null) throw new ModelBusyException(_downloadingId == id ? "このモデルは取得中です" : $"別のモデル（{_downloadingId}）を取得中です");
            _downloadingId = id;
        }
        var part = PartPathOf(id);
        try
        {
            var existing = new FileInfo(PathOf(id));
            // 検証済みの既存は保持し、再取得しない。
            if (existing.Exists && (await VerifyAsync(e, ct).ConfigureAwait(false)).State == ModelState.Ready) return;
            if (existing.Exists && _isInUse(id)) throw new ModelInUseException("このモデルは読み込み中のため置き換えられません。モデルを切り替えてから再取得してください");

            Directory.CreateDirectory(_dir);
            EnsureFreeSpace(e.SizeBytes);
            if (File.Exists(part)) File.Delete(part);

            using var client = _network.CreateClient(NetworkPurpose.ModelDownload);
            using var response = await client.GetAsync(e.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode != System.Net.HttpStatusCode.OK)
                throw new ModelException($"モデルを取得できません（HTTP {(int)response.StatusCode}）");
            if (response.Content.Headers.ContentLength is { } cl && cl != e.SizeBytes)
                throw new ModelException($"サーバーが示すサイズが catalog と違います（{cl} / {e.SizeBytes} bytes）");

            string hash;
            long done = 0;
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buf = new byte[BufferSize];
                var clock = Stopwatch.StartNew();
                long lastReport = -1000;
                int n;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    done += n;
                    if (done > e.SizeBytes) throw new ModelException("想定より大きいデータを受信したため中止しました");
                    hasher.AppendData(buf, 0, n);
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    if (clock.ElapsedMilliseconds - lastReport >= 200)
                    {
                        lastReport = clock.ElapsedMilliseconds;
                        progress?.Report(new DownloadProgress(id, done, e.SizeBytes));
                    }
                }
                await dst.FlushAsync(ct).ConfigureAwait(false);
                dst.Flush(true);
                hash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            }
            if (done != e.SizeBytes) throw new ModelException($"途中で切断されました（{done} / {e.SizeBytes} bytes）");
            if (!string.Equals(hash, e.Sha256, StringComparison.Ordinal)) throw new ModelException("SHA-256 が一致しないため破棄しました（ファイルが改ざん・破損している可能性があります）");

            try { File.Move(part, PathOf(id), overwrite: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new ModelInUseException("モデルファイルが使用中のため置き換えられませんでした: " + ex.Message); }
            var fi = new FileInfo(PathOf(id));
            lock (_lock) _verified[id] = (fi.Length, fi.LastWriteTimeUtc.Ticks, true);
            progress?.Report(new DownloadProgress(id, e.SizeBytes, e.SizeBytes));
        }
        finally
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { }
            lock (_lock) _downloadingId = null;
        }
    }

    private void EnsureFreeSpace(long need)
    {
        long free;
        try { free = _freeSpace?.Invoke() ?? new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_dir))!).AvailableFreeSpace; }
        catch { return; } // 取得できない環境では検査を省く（書き込み失敗で検出される）
        if (free < need + FreeSpaceMargin)
            throw new ModelException($"空き容量が足りません（必要 約 {(need + FreeSpaceMargin) / (1024 * 1024)} MiB、空き {free / (1024 * 1024)} MiB）");
    }
}
