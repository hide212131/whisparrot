using System.Security.Cryptography;

namespace Whisparrot.Core.Update;

/// <summary>GitHub が資産に付ける <c>sha256:&lt;64 桁 hex&gt;</c> の解釈と照合。</summary>
public static class Sha256Digest
{
    public static bool TryParse(string? digest, out string hex)
    {
        hex = "";
        const string prefix = "sha256:";
        if (digest is null || !digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var h = digest[prefix.Length..];
        if (h.Length != 64 || !h.All(Uri.IsHexDigit)) return false;
        hex = h.ToLowerInvariant();
        return true;
    }

    public static async Task<string> ComputeAsync(string path, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false));
    }

    public static async Task<bool> MatchesAsync(string path, string expectedHex, CancellationToken ct = default) =>
        string.Equals(await ComputeAsync(path, ct).ConfigureAwait(false), expectedHex, StringComparison.OrdinalIgnoreCase);
}
