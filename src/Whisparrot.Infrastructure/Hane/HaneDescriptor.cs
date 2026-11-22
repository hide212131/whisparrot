using System.Text.Json;
using System.Text.RegularExpressions;
using Whisparrot.Core.Hane;

namespace Whisparrot.Infrastructure.Hane;

/// <summary>
/// Hane が公開する接続記述子（設計 §2）。<c>%LOCALAPPDATA%/Hane/integration/whisparrot/&lt;pid&gt;.json</c>。
/// W-1 時点で Hane 側（H-1）の項目名は未確定のため、設計の記述（PID・プロセス作成時刻・パイプ名・対応版・instanceId・トークン）に沿う次の形を仮定する:
/// <code>{ "pid": 123, "processCreationTime": "&lt;FILETIME 十進&gt;", "instanceId": "...", "pipeName": "Hane.Whisparrot.123.&lt;instanceId&gt;",
///   "token": "...", "protocol": { "min": { "major": 1, "minor": 0 }, "max": { "major": 1, "minor": 0 } } }</code>
/// トークンは接続の認証にだけ使い、ログ・診断・<see cref="ToString"/> に出さない。
/// </summary>
internal sealed record HaneDescriptor(uint Pid, long ProcessCreationFileTime, string InstanceId, string PipeName, string Token, HaneVersion MinVersion, HaneVersion MaxVersion)
{
    public override string ToString() => $"HaneDescriptor(pid={Pid})";
}

internal static partial class HaneDescriptorReader
{
    internal const long MaxFileBytes = 16 * 1024;

    // パイプ名は固定形式のローカル名に限る（\\.\pipe\ 以外・リモート・任意名を拒否する）。
    [GeneratedRegex(@"^Hane\.Whisparrot\.(?<pid>[0-9]{1,10})\.(?<iid>[A-Za-z0-9]{8,64})$", RegexOptions.CultureInvariant)]
    private static partial Regex PipeNamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9_\-+/=]{32,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    /// <summary>
    /// 窓の PID の記述子を一つだけ読み、形式・再解析ポイント・PID と作成時刻を検証する。
    /// <paramref name="trustedRoot"/> から <paramref name="directory"/> までの各ディレクトリとファイルが再解析ポイントなら拒否する。
    /// </summary>
    public static (HaneDescriptor? Descriptor, HaneFailure Failure) Read(string directory, string trustedRoot, uint pid)
    {
        try
        {
            var path = Path.Combine(directory, pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".json");
            if (!File.Exists(path)) return (null, HaneFailure.NoDescriptor);
            if (HasReparsePoint(directory, trustedRoot) || new FileInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint))
                return (null, HaneFailure.BadDescriptor);
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > MaxFileBytes) return (null, HaneFailure.BadDescriptor);

            using var doc = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, HaneFailure.BadDescriptor);

            if (!TryUInt(root, "pid", out var filePid) || filePid != pid) return (null, HaneFailure.BadDescriptor);
            if (!TryFileTime(root, "processCreationTime", out var created)) return (null, HaneFailure.BadDescriptor);
            if (Str(root, "instanceId") is not { } iid || Str(root, "pipeName") is not { } pipe || Str(root, "token") is not { } token)
                return (null, HaneFailure.BadDescriptor);
            var m = PipeNamePattern().Match(pipe);
            if (!m.Success || m.Groups["pid"].Value != pid.ToString(System.Globalization.CultureInfo.InvariantCulture) || m.Groups["iid"].Value != iid)
                return (null, HaneFailure.BadDescriptor);
            if (!TokenPattern().IsMatch(token)) return (null, HaneFailure.BadDescriptor);
            if (!root.TryGetProperty("protocol", out var proto) || !TryVersion(proto, "min", out var min) || !TryVersion(proto, "max", out var max))
                return (null, HaneFailure.BadDescriptor);

            // 異常終了の残骸は PID・作成時刻の不一致で無効（PID の再利用を含む）。
            if (HaneNative.ProcessCreationFileTime(pid) is not { } actual || actual != created) return (null, HaneFailure.StaleInstance);

            return (new HaneDescriptor(pid, created, iid, pipe, token, min, max), HaneFailure.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return (null, HaneFailure.BadDescriptor);
        }
    }

    private static bool HasReparsePoint(string directory, string trustedRoot)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trustedRoot));
        var dir = new DirectoryInfo(Path.GetFullPath(directory));
        for (var d = dir; d is not null; d = d.Parent)
        {
            if (d.Attributes.HasFlag(FileAttributes.ReparsePoint)) return true;
            if (string.Equals(Path.TrimEndingDirectorySeparator(d.FullName), root, StringComparison.OrdinalIgnoreCase)) break;
        }
        return false;
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool TryUInt(JsonElement o, string name, out uint value)
    {
        value = 0;
        return o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetUInt32(out value);
    }

    private static bool TryFileTime(JsonElement o, string name, out long value)
    {
        value = 0;
        if (!o.TryGetProperty(name, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.String => long.TryParse(v.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value),
            JsonValueKind.Number => v.TryGetInt64(out value),
            _ => false,
        };
    }

    private static bool TryVersion(JsonElement o, string name, out HaneVersion version)
    {
        version = new HaneVersion(0, 0);
        if (!o.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Object) return false;
        if (!v.TryGetProperty("major", out var ma) || !ma.TryGetInt32(out var major)) return false;
        if (!v.TryGetProperty("minor", out var mi) || !mi.TryGetInt32(out var minor)) return false;
        version = new HaneVersion(major, minor);
        return true;
    }
}
