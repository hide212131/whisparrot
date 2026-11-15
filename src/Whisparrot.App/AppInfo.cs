using System.Reflection;

namespace Whisparrot.App;

internal static class AppInfo
{
    /// <summary>native 展開先のバージョン階層に使う（<c>runtime\&lt;version&gt;\&lt;arch&gt;\</c>）。</summary>
    public static string Version { get; } =
        (Assembly.GetEntryAssembly() ?? typeof(AppInfo).Assembly).GetName().Version?.ToString(3) ?? "0.0.0";
}
