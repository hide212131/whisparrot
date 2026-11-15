using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Whisparrot.Infrastructure.Recognition;

/// <summary>exe に埋め込んだ native DLL の一覧と読み出し。リソース名は <c>native/&lt;相対パス&gt;</c>。</summary>
public interface INativeResourceSource
{
    IReadOnlyList<string> Names { get; }
    Stream Open(string name);
}

public sealed class AssemblyResourceSource(Assembly assembly) : INativeResourceSource
{
    public IReadOnlyList<string> Names { get; } =
        assembly.GetManifestResourceNames().Where(n => n.StartsWith(NativeRuntime.ResourcePrefix, StringComparison.Ordinal)).ToArray();

    public Stream Open(string name) =>
        assembly.GetManifestResourceStream(name) ?? throw new FileNotFoundException(name);
}

/// <summary>
/// 設計 §2.2 の自前展開。埋め込んだ native DLL を
/// <c>%LOCALAPPDATA%\whisparrot\runtime\&lt;version&gt;\&lt;arch&gt;\</c> へ SHA-256 確認付きで原子的に配置する。
/// 展開先は再生成可能な実行キャッシュで、利用者が持ち運ぶフォルダには何も置かない。
/// </summary>
public sealed class NativeRuntime
{
    public const string ResourcePrefix = "native/";
    private const string VcrtDir = "vcrt/";

    // アプリローカルで先読みする VC++ ランタイム（依存順）。
    private static readonly string[] VcrtPreload = ["vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll", "vcomp140.dll"];

    private readonly INativeResourceSource _source;
    private readonly string _root;
    private readonly string _arch;

    public NativeRuntime(INativeResourceSource source, string cacheRoot, string version, string arch)
    {
        _source = source;
        _arch = arch;
        _root = Path.Combine(cacheRoot, version, arch);
    }

    public string Directory => _root;

    /// <summary>埋め込まれているバックエンド（常に "cpu" を含むとは限らない）。</summary>
    public IReadOnlySet<string> AvailableBackends
    {
        get
        {
            var set = new HashSet<string>();
            foreach (var n in _source.Names)
            {
                var rel = n[ResourcePrefix.Length..];
                if (rel.EndsWith("/whisper.dll", StringComparison.Ordinal))
                    set.Add(rel.Contains("/vulkan/", StringComparison.Ordinal) ? "vulkan" : "cpu");
            }
            return set;
        }
    }

    /// <summary>指定バックエンドの <c>whisper.dll</c> の相対パス。無ければ null。</summary>
    public string? WhisperDllRelative(string backend)
    {
        var want = backend == "vulkan" ? $"runtimes/vulkan/{_arch}/whisper.dll" : $"runtimes/{_arch}/whisper.dll";
        return _source.Names.Contains(ResourcePrefix + want) ? want : null;
    }

    /// <summary>全リソースを展開（変更が無ければ何もしない）。展開した/済みのファイル数を返す。</summary>
    public (int Written, int Reused) Extract()
    {
        System.IO.Directory.CreateDirectory(_root);
        int written = 0, reused = 0;
        foreach (var name in _source.Names)
        {
            var rel = name[ResourcePrefix.Length..];
            var target = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
            if (ExtractOne(name, target)) written++; else reused++;
        }
        return (written, reused);
    }

    private bool ExtractOne(string resourceName, string target)
    {
        byte[] data;
        using (var s = _source.Open(resourceName))
        using (var ms = new MemoryStream())
        {
            s.CopyTo(ms);
            data = ms.ToArray();
        }
        var expected = SHA256.HashData(data);

        if (File.Exists(target) && new FileInfo(target).Length == data.Length && HashFile(target).AsSpan().SequenceEqual(expected))
            return false;

        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var tmp = target + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllBytes(tmp, data);
        try
        {
            File.Move(tmp, target, overwrite: true);
        }
        catch (IOException)
        {
            // 他プロセスが同じファイルを使用中/同時展開: 内容が一致していれば成功とみなす。
            File.Delete(tmp);
            if (!(File.Exists(target) && HashFile(target).AsSpan().SequenceEqual(expected))) throw;
            return false;
        }
        return true;
    }

    private static byte[] HashFile(string path)
    {
        using var fs = File.OpenRead(path);
        return SHA256.HashData(fs);
    }

    /// <summary>
    /// 展開済みの VC++ ランタイムを先にロードし、後続の whisper.dll などの依存解決をアプリローカルの版に向ける。
    /// ロードした DLL 名を返す。
    /// </summary>
    public IReadOnlyList<string> PreloadVcRuntime()
    {
        var loaded = new List<string>();
        var dir = Path.Combine(_root, "vcrt");
        foreach (var name in VcrtPreload)
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path) && NativeLibrary.TryLoad(path, out _)) loaded.Add(name);
        }
        return loaded;
    }

    /// <summary>x64 / arm64 の RID 名（"win-x64" / "win-arm64"）。</summary>
    public static string CurrentRid() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => "win-arm64",
        Architecture.X64 => "win-x64",
        var a => throw new PlatformNotSupportedException($"未対応のアーキテクチャ: {a}"),
    };
}
