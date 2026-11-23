using System.Text.RegularExpressions;

namespace Whisparrot.Core.Update;

public sealed record ReleaseAsset(string Name, long Size, string DownloadUrl, string? Digest);

/// <summary>GitHub Releases の 1 件（必要な項目だけ）。</summary>
public sealed record ReleaseInfo(string Tag, bool Prerelease, bool Draft, IReadOnlyList<ReleaseAsset> Assets);

/// <summary>適用できる更新。<c>Sha256</c> は小文字 hex 64 桁（GitHub の資産 digest 由来）。</summary>
public sealed record UpdateCandidate(SemVer Version, string Tag, bool Prerelease, string AssetName, long Size, string DownloadUrl, string Sha256);

public static class ReleaseSelector
{
    /// <summary>
    /// 現在の版より新しい最大の版を選ぶ。ドラフトは対象外。プレリリースは <paramref name="includePrerelease"/> のときだけ
    /// （タグが SemVer のプレリリース表記でも、Release 側のフラグでも、プレリリースと扱う）。
    /// 資産は <paramref name="arch"/>（x64 / arm64）に合う <c>whisparrot*-&lt;arch&gt;.exe</c> のうち、検証用の SHA-256 digest を持つものだけ。
    /// </summary>
    public static UpdateCandidate? Select(IEnumerable<ReleaseInfo> releases, SemVer current, bool includePrerelease, string arch)
    {
        UpdateCandidate? best = null;
        foreach (var r in releases)
        {
            if (r.Draft) continue;
            if (!SemVer.TryParse(r.Tag, out var v)) continue;
            var pre = r.Prerelease || v.IsPrerelease;
            if (pre && !includePrerelease) continue;
            if (v <= current) continue;
            if (best is not null && v <= best.Version) continue;
            var asset = PickAsset(r.Assets, arch);
            if (asset is null || !Sha256Digest.TryParse(asset.Digest, out var hash)) continue;
            best = new UpdateCandidate(v, r.Tag, pre, asset.Name, asset.Size, asset.DownloadUrl, hash);
        }
        return best;
    }

    /// <summary>arch が一致する exe 資産を 1 つ選ぶ。別 arch（arm64 と x64）は取り違えない。曖昧（複数一致）なら選ばない。</summary>
    public static ReleaseAsset? PickAsset(IEnumerable<ReleaseAsset> assets, string arch)
    {
        if (arch is not ("x64" or "arm64")) return null;
        var re = new Regex($@"^whisparrot[A-Za-z0-9._-]*-{arch}\.exe$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var hits = assets.Where(a => re.IsMatch(a.Name)).ToList();
        return hits.Count == 1 ? hits[0] : null;
    }
}
