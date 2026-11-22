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
        if (args.Length > 0 && args[0] == "--clipboard-probe") return Diagnostics.ClipboardProbe(args);
        if (args.Length > 0 && args[0] == "--inject-probe") return Diagnostics.InjectProbe(args);
        if (args.Length > 0 && args[0] == "--pill-probe") return Diagnostics.PillProbe();
        if (args.Length > 0 && args[0] == "--record-probe") return Diagnostics.RecordProbe(args);
        if (args.Length > 0 && args[0] == "--settings-probe") return Diagnostics.SettingsProbe(args);
        if (args.Length > 0 && args[0] == "--settings-window-probe") return Diagnostics.SettingsWindowProbe(args);
        if (args.Length > 0 && args[0] == "--autostart-probe") return Diagnostics.AutostartProbe();
        if (args.Length > 0 && args[0] == "--model-download") return Diagnostics.ModelDownload(args);
        if (args.Length > 0 && args[0] == "--write-cleanup-config") return Diagnostics.WriteCleanupConfig(args);
        if (args.Length > 0 && args[0] == "--cleanup-probe") return Diagnostics.CleanupProbe(args);
        if (args.Length > 0 && args[0] == "--chatgpt-login") return Diagnostics.ChatGptLogin(args);
        if (args.Length > 0 && args[0] == "--chatgpt-status") return Diagnostics.ChatGptStatusProbe(args);
        if (args.Length > 0 && args[0] == "--chatgpt-logout") return Diagnostics.ChatGptLogout(args);
        if (args.Length > 0 && args[0] == "--write-transcription-config") return Diagnostics.WriteTranscriptionConfig(args);
        if (args.Length > 0 && args[0] == "--transcription-probe") return Diagnostics.TranscriptionProbe(args);
        if (args.Length > 0 && args[0] == "--hane-probe") return Diagnostics.HaneProbe(args);
        if (args.Length > 0 && args[0] == "--pipeline-file") return Diagnostics.PipelineFile(args, resources);

        using var mutex = new Mutex(true, SingletonMutexName, out var created);
        if (!created) return 0; // 二重起動: 静かに終了する

        return TrayApp.Run(resources);
    }
}
