using System.Text.Json;
using Whisparrot.Core.Update;
using Whisparrot.Infrastructure.Network;

namespace Whisparrot.Infrastructure.Update;

/// <summary>GitHub Releases の取得・資産のダウンロード。通信は必ず <see cref="NetworkPurpose.Update"/>（HTTPS・GitHub ホストのみ転送可）。</summary>
public sealed class ReleaseClient(NetworkContext network, string repo = ReleaseClient.DefaultRepo)
{
    public const string DefaultRepo = "hide212131/whisparrot";

    public async Task<IReadOnlyList<ReleaseInfo>> ListAsync(CancellationToken ct = default)
    {
        // /releases/latest はプレリリースを返さないので、一覧を取る。
        using var http = network.CreateClient(NetworkPurpose.Update);
        using var req = Request($"https://api.github.com/repos/{repo}/releases?per_page=30");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var res = await http.SendAsync(req, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        await using var body = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return ParseReleases(await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false));
    }

    public static IReadOnlyList<ReleaseInfo> ParseReleases(JsonDocument doc)
    {
        var list = new List<ReleaseInfo>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            var assets = new List<ReleaseAsset>();
            if (r.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var a in arr.EnumerateArray())
                    assets.Add(new ReleaseAsset(Str(a, "name") ?? "", a.TryGetProperty("size", out var sz) && sz.TryGetInt64(out var n) ? n : -1,
                        Str(a, "browser_download_url") ?? "", Str(a, "digest")));
            list.Add(new ReleaseInfo(Str(r, "tag_name") ?? "", Bool(r, "prerelease"), Bool(r, "draft"), assets));
        }
        return list;
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static HttpRequestMessage Request(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("whisparrot-updater");
        return req;
    }

    /// <summary>
    /// 資産を <paramref name="destination"/> へ取得する。一時ファイル（.part）へ書き、サイズと SHA-256 を照合してから置く。
    /// 不一致・失敗では一時ファイルを消し、<paramref name="destination"/> には何も残さない。
    /// </summary>
    public async Task DownloadAsync(UpdateCandidate c, string destination, IProgress<(long Done, long Total)>? progress = null, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(c.DownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !RedirectPolicy.GitHub.IsHostAllowed(uri))
            throw new UpdateException("ダウンロード元が GitHub の HTTPS ではありません");

        var part = destination + ".part";
        try
        {
            using (var http = network.CreateClient(NetworkPurpose.Update, timeout: Timeout.InfiniteTimeSpan))
            using (var req = Request(c.DownloadUrl))
            using (var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                res.EnsureSuccessStatusCode();
                await using var src = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024, useAsync: true);
                var buf = new byte[256 * 1024];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                    done += n;
                    progress?.Report((done, c.Size));
                }
            }
            var verify = await VerifyFileAsync(part, c, ct).ConfigureAwait(false);
            if (verify is not null) throw new UpdateException(verify);
            File.Move(part, destination, overwrite: true);
        }
        finally
        {
            try { File.Delete(part); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>サイズと SHA-256 の照合。問題がなければ null、あれば利用者向けの理由。</summary>
    public static async Task<string?> VerifyFileAsync(string path, UpdateCandidate c, CancellationToken ct = default)
    {
        if (c.Size >= 0 && new FileInfo(path).Length != c.Size) return "ダウンロードしたファイルのサイズが一致しません。破棄しました";
        return await Sha256Digest.MatchesAsync(path, c.Sha256, ct).ConfigureAwait(false)
            ? null
            : "ダウンロードしたファイルの SHA-256 が GitHub の値と一致しません。破棄しました";
    }
}

public sealed class UpdateException(string message) : Exception(message);
