using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Spike.S01;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [STAThread]
    private static int Main(string[] args)
    {
        // WinExe のまま、親コンソール（cmd / SSH）へ出力できるようにする。
        AttachConsole(-1);

        if (args.Length > 0 && args[0] == "--recognize")
        {
            return Recognize.Run(args);
        }

        if (args.Length > 0 && args[0] == "--recognize-isolated")
        {
            return IsolatedRecognize.Run(args);
        }

        // 起動時間計測: プロセス開始からピル表示までの ms と、待機時 working set を出して終了する。
        var bench = args.Length > 0 && args[0] == "--bench-startup";
        return TrayApp.Run(bench);
    }

    internal static double MsSinceProcessStart() =>
        (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
}
