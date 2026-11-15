using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Whisparrot.Core.Settings;

namespace Whisparrot.Infrastructure.Models;

public sealed record ModelEntry(string Id, string Filename, Uri Url, string Revision, long SizeBytes, string Sha256, string License);

/// <summary>
/// 設計 §4.2 の固定 catalog（exe に埋め込み）。リリースでは可変の <c>main</c>・未確定 SHA・空サイズを許さない。
/// 値の出典は Hugging Face の公開メタデータ（LFS の sha256 とサイズ）で、<c>build/verify-catalog.ps1</c> が再照合する。
/// </summary>
public sealed class ModelCatalog
{
    private static readonly Regex Sha256Rx = new("^[0-9a-f]{64}$", RegexOptions.Compiled);
    private static readonly Regex RevisionRx = new("^[0-9a-f]{40}$", RegexOptions.Compiled);

    private readonly Dictionary<string, ModelEntry> _byId;

    private ModelCatalog(Dictionary<string, ModelEntry> byId) => _byId = byId;

    public IReadOnlyCollection<ModelEntry> Entries => _byId.Values;

    public ModelEntry Get(string id) => _byId.TryGetValue(id, out var e) ? e : throw new KeyNotFoundException($"catalog にないモデル: {id}");

    public static ModelCatalog Embedded()
    {
        var asm = typeof(ModelCatalog).Assembly;
        using var s = asm.GetManifestResourceStream("model-catalog.json") ?? throw new InvalidOperationException("model-catalog.json が埋め込まれていません");
        return Parse(s);
    }

    public static ModelCatalog Parse(Stream json, bool requireAllModels = true)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var map = new Dictionary<string, ModelEntry>();
        foreach (var m in root.GetProperty("models").EnumerateArray())
        {
            var id = m.GetProperty("id").GetString()!;
            var filename = m.GetProperty("filename").GetString()!;
            var url = new Uri(m.GetProperty("url").GetString()!);
            var size = m.GetProperty("size_bytes").GetInt64();
            var sha = m.GetProperty("sha256").GetString()!;
            var license = m.GetProperty("license").GetString()!;
            var revision = ExtractRevision(url);

            if (!ModelIds.All.Contains(id)) throw new InvalidDataException($"未知のモデル ID: {id}");
            if (map.ContainsKey(id)) throw new InvalidDataException($"モデル ID が重複: {id}");
            if (filename.Length == 0 || filename != Path.GetFileName(filename) || filename.Contains("..") || !filename.EndsWith(".bin") || filename.Contains(".en."))
                throw new InvalidDataException($"{id}: filename が不正です");
            if (url.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException($"{id}: URL は HTTPS のみ");
            if (!RevisionRx.IsMatch(revision)) throw new InvalidDataException($"{id}: URL に固定 revision（40 桁 hex）がありません（main 等は不可）");
            if (!url.AbsolutePath.EndsWith("/" + filename, StringComparison.Ordinal)) throw new InvalidDataException($"{id}: URL の末尾が filename と一致しません");
            if (size <= 0) throw new InvalidDataException($"{id}: size_bytes が不正です");
            if (!Sha256Rx.IsMatch(sha)) throw new InvalidDataException($"{id}: sha256 は小文字 64 桁 hex");
            map[id] = new ModelEntry(id, filename, url, revision, size, sha, license);
        }
        if (requireAllModels)
        {
            var missing = ModelIds.All.Where(i => !map.ContainsKey(i)).ToList();
            if (missing.Count > 0) throw new InvalidDataException("catalog に不足: " + string.Join(", ", missing));
        }
        return new ModelCatalog(map);
    }

    /// <summary><c>.../resolve/&lt;revision&gt;/&lt;file&gt;</c> の revision 部分。無ければ空文字。</summary>
    private static string ExtractRevision(Uri url)
    {
        var parts = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var i = Array.IndexOf(parts, "resolve");
        return i >= 0 && i + 1 < parts.Length ? parts[i + 1] : "";
    }
}
