using System.Reflection;
using Whisparrot.Core;

namespace Whisparrot.App;

internal static class AppInfo
{
    /// <summary>native 展開先のバージョン階層に使う（<c>runtime\&lt;version&gt;\&lt;arch&gt;\</c>）。</summary>
    public static string Version { get; } =
        (Assembly.GetEntryAssembly() ?? typeof(AppInfo).Assembly).GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>画面表示用（例: <c>v0.2.0</c>）。exe の AssemblyInformationalVersion から読む。</summary>
    public static string DisplayVersion { get; } = ReadDisplayVersion();

    private static string ReadDisplayVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(AppInfo).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return VersionText.Format(info, asm.GetName().Version);
    }
}
