using System.Windows;
using Whisparrot.App.Views;
using Whisparrot.App.Windows;
using Whisparrot.Core;
using Whisparrot.Infrastructure;
using Whisparrot.Infrastructure.Audio;
using Whisparrot.Infrastructure.Recognition;

namespace Whisparrot.App;

/// <summary>トレイ常駐の組み立て（設計 §3.1 の「起動時の構成箇所」）。Core の具象への結線はここだけ。</summary>
internal static class TrayApp
{
    public static int Run(INativeResourceSource resources)
    {
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        var native = new NativeRuntime(resources, AppPaths.RuntimeCacheRoot, AppInfo.Version, NativeRuntime.CurrentRid());
        var forceCpu = string.Equals(Environment.GetEnvironmentVariable("WHISPARROT_BACKEND"), "cpu", StringComparison.OrdinalIgnoreCase);
        var recognizer = new WhisperRecognizer(new WhisperRecognizerOptions(
            AppPaths.DefaultModelPath,
            forceCpu ? ["cpu"] : ["vulkan", "cpu"],
            native.AvailableBackends,
            WhisperRecognizer.SmallModelBytes));
        var recorder = new WasapiRecorder();
        var injector = new TextInjector();
        var coordinator = new SessionCoordinator(recorder, recognizer, injector);

        var pill = new PillWindow();
        var hotkey = new HotkeyService();
        var store = new InMemoryHotkeyStore();
        using var tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "whisparrot",
            Visible = true,
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        var toggleItem = menu.Items.Add("開始 / 停止", null, (_, _) => coordinator.Toggle());
        var cancelItem = menu.Items.Add("取消", null, (_, _) => coordinator.Cancel());
        var retryItem = menu.Items.Add("入力を再試行", null, (_, _) => coordinator.RetryInsert());
        retryItem.Visible = false;
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => app.Dispatcher.BeginInvoke(app.Shutdown));
        tray.ContextMenuStrip = menu;

        pill.StopClicked += coordinator.Stop;
        pill.CancelClicked += coordinator.Cancel;
        recorder.LevelChanged += level => app.Dispatcher.BeginInvoke(() => pill.PushLevel(level));
        coordinator.StateChanged += s => app.Dispatcher.BeginInvoke(() =>
        {
            pill.ShowState(s.State);
            retryItem.Visible = s.State == SessionState.Failed;
        });
        coordinator.Noticed += n => app.Dispatcher.BeginInvoke(() =>
            tray.ShowBalloonTip(2500, "whisparrot", n.Message,
                n.Kind == NoticeKind.Error ? System.Windows.Forms.ToolTipIcon.Warning : System.Windows.Forms.ToolTipIcon.Info));
        hotkey.Pressed += () => coordinator.Toggle();

        app.Startup += (_, _) =>
        {
            var adopted = hotkey.Register(HotkeyNegotiator.DefaultCandidates);
            store.Record(adopted, hotkey.Attempts);
            tray.Text = adopted is null ? "whisparrot（ホットキー未登録: トレイから操作）" : $"whisparrot（{adopted.Display}）";
            foreach (var (display, ok, error) in hotkey.Attempts)
                Console.WriteLine($"hotkey {display}: {(ok ? "OK" : $"FAILED win32_error={error}")}");
            Console.WriteLine($"hotkey_adopted={adopted?.Display ?? "(none)"}");
            if (adopted is null)
                tray.ShowBalloonTip(4000, "whisparrot", "ホットキーを登録できませんでした。トレイのメニューから開始/停止できます。", System.Windows.Forms.ToolTipIcon.Warning);
        };
        app.Exit += (_, _) =>
        {
            hotkey.Dispose();
            coordinator.Dispose();
            recognizer.Dispose();
            recorder.Dispose();
            tray.Visible = false;
        };
        return app.Run();
    }

    private sealed class InMemoryHotkeyStore : IAdoptedHotkeyStore
    {
        public Hotkey? Adopted { get; private set; }

        public void Record(Hotkey? adopted, IReadOnlyList<(string Display, bool Ok, int Error)> attempts) => Adopted = adopted;
    }
}
