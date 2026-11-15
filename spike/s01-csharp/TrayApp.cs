using System.Diagnostics;
using System.Windows;

namespace Spike.S01;

/// <summary>トレイ常駐 + ピル placeholder の最小実装。</summary>
internal static class TrayApp
{
    public static int Run(bool bench)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        var pill = new PillWindow();
        var tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "spike-s01",
            Visible = true,
        };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("ピルを表示/隠す", null, (_, _) => pill.Toggle());
        menu.Items.Add("終了", null, (_, _) => app.Shutdown());
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left) pill.Toggle();
        };

        app.Startup += (_, _) =>
        {
            pill.Toggle();
            if (!bench) return;
            var shownMs = Program.MsSinceProcessStart();
            // 待機状態が落ち着くまで待ってから working set を採る。
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                var p = Process.GetCurrentProcess();
                p.Refresh();
                Console.WriteLine($"startup_to_pill_ms={shownMs:F0}");
                Console.WriteLine($"idle_working_set_mib={p.WorkingSet64 / 1048576.0:F1}");
                Console.WriteLine($"idle_private_mib={p.PrivateMemorySize64 / 1048576.0:F1}");
                Console.WriteLine($"process_arch={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
                Console.Out.Flush();
                app.Shutdown();
            };
            timer.Start();
        };
        app.Exit += (_, _) => tray.Dispose();
        return app.Run();
    }
}
