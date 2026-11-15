using Whisparrot.App.Windows;
using Whisparrot.Infrastructure.Recognition;

namespace Whisparrot.App;

internal static class Program
{
    private const string SingletonMutexName = @"Local\whisparrot-singleton";

    [STAThread]
    private static int Main(string[] args)
    {
        // WinExe のまま、親コンソール（cmd / SSH）へ診断出力できるようにする。
        NativeMethods.AttachConsole(-1);

        var resources = new AssemblyResourceSource(typeof(Program).Assembly);

        // 認識ワーカー: 二重起動の mutex には掛けない（親が起動する子）。
        if (args.Contains(WorkerHost.Flag)) return WorkerHost.Run(args, resources, AppInfo.Version);

        if (args.Length > 0 && args[0] == "--native-info") return Diagnostics.NativeInfo(resources);
        if (args.Length > 0 && args[0] == "--pipeline-file") return Diagnostics.PipelineFile(args, resources);

        using var mutex = new Mutex(true, SingletonMutexName, out var created);
        if (!created) return 0; // 二重起動: 静かに終了する

        return TrayApp.Run(resources);
    }
}
