using System.Windows;
using Whisparrot.App.Views;
using Whisparrot.App.Windows;
using Whisparrot.Core;
using Whisparrot.Core.Settings;
using Whisparrot.Infrastructure;
using Whisparrot.Infrastructure.Audio;
using Whisparrot.Infrastructure.Models;
using Whisparrot.Infrastructure.Network;
using Whisparrot.Infrastructure.Recognition;
using Whisparrot.Infrastructure.Settings;

namespace Whisparrot.App;

/// <summary>トレイ常駐の組み立て（設計 §3.1 の「起動時の構成箇所」）。Core の具象への結線はここだけ。</summary>
internal static class TrayApp
{
    public static int Run(INativeResourceSource resources)
    {
        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        // 設定。読み込めなくても（未来 schema・壊れた JSON）起動はするが、ホットキーは有効化せず、ファイルにも触れない（設計 §8.2）。
        var settings = new SettingsService(new JsonSettingsStore(AppPaths.ConfigPath));
        var protector = new DpapiSecretProtector();
        var autostart = new AutostartService(Environment.ProcessPath!);
        var network = new NetworkContext(() => ProxyConfig.FromSettings(settings.Current.Proxy, protector), ProxyEnvironment.Capture());
        WhisperRecognizer recognizer = null!;
        ModelManager models = null!;
        models = new ModelManager(AppPaths.ModelsDirectory, ModelCatalog.Embedded(), network,
            isInUse: id => recognizer?.LoadedModelPath is { } p && string.Equals(p, models.PathOf(id), StringComparison.OrdinalIgnoreCase));

        var native = new NativeRuntime(resources, AppPaths.RuntimeCacheRoot, AppInfo.Version, NativeRuntime.CurrentRid());
        var forceCpu = string.Equals(Environment.GetEnvironmentVariable("WHISPARROT_BACKEND"), "cpu", StringComparison.OrdinalIgnoreCase);
        recognizer = new WhisperRecognizer(new WhisperRecognizerOptions(
            "", forceCpu ? ["cpu"] : ["vulkan", "cpu"], native.AvailableBackends,
            ModelResolver: () =>
            {
                var id = settings.Current.Recognition.Model;
                return new ModelTarget(models.PathOf(id), models.CheckReady(id));
            }));
        var recorder = new WasapiRecorder(() => settings.Current.MicrophoneId);
        var injector = new TextInjector();
        var coordinator = new SessionCoordinator(recorder, recognizer, injector);

        var pill = new PillWindow();
        var hotkey = new HotkeyService();
        using var tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "whisparrot",
            Visible = true,
        };

        SettingsWindow? settingsWindow = null;
        void OpenSettings(string? notice = null)
        {
            if (settingsWindow is not null) { settingsWindow.Activate(); return; }
            settingsWindow = new SettingsWindow(settings, protector, models, network, autostart,
                hk => (hotkey.TryReplace(hk, out var err), err), () => hotkey.Adopted, () => recognizer.LastBackend, notice);
            settingsWindow.Closed += (_, _) => settingsWindow = null;
            settingsWindow.Show();
            settingsWindow.Activate();
        }

        void UpdateTrayText()
        {
            var hk = hotkey.Adopted;
            tray.Text = !settings.LoadResult.Usable ? "whisparrot（設定エラー）"
                : hk is null ? "whisparrot（ホットキー未登録: トレイから操作）" : $"whisparrot（{hk.Display}）";
        }

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("設定", null, (_, _) => OpenSettings());
        var toggleItem = menu.Items.Add("開始 / 停止", null, (_, _) => coordinator.Toggle());
        var cancelItem = menu.Items.Add("取消", null, (_, _) => coordinator.Cancel());
        var retryItem = menu.Items.Add("入力を再試行", null, (_, _) => coordinator.RetryInsert());
        retryItem.Visible = false;
        var resetItem = menu.Items.Add("壊れた設定ファイルを退避して初期化", null, (_, _) =>
        {
            try
            {
                var backup = settings.BackupAndReset();
                tray.ShowBalloonTip(4000, "whisparrot", $"config.json を {System.IO.Path.GetFileName(backup)} へ退避しました。再起動するとホットキーが有効になります。", System.Windows.Forms.ToolTipIcon.Info);
            }
            catch (Exception ex) { tray.ShowBalloonTip(4000, "whisparrot", ex.Message, System.Windows.Forms.ToolTipIcon.Warning); }
        });
        resetItem.Visible = settings.LoadResult.Status is SettingsLoadStatus.Corrupt or SettingsLoadStatus.Invalid;
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("終了", null, (_, _) => app.Dispatcher.BeginInvoke(() => app.Shutdown()));
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (_, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) app.Dispatcher.BeginInvoke(() => OpenSettings()); };

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

        // 適用範囲（設計 §3.2）: ホットキー・自動起動は設定画面の保存時に即時。モデル・マイクは次のセッションから。
        // プロキシは次の通信（クライアント生成）から。ここでは待機中の認識子だけを解放する。
        settings.Changed += (old, next) => app.Dispatcher.BeginInvoke(() =>
        {
            if (old.Recognition.Model != next.Recognition.Model) recognizer.ReleaseIdleWorker();
            UpdateTrayText();
        });

        app.Startup += (_, _) =>
        {
            if (!settings.LoadResult.Usable)
            {
                Console.WriteLine("settings_error=" + settings.LoadResult.Status);
                UpdateTrayText();
                tray.ShowBalloonTip(6000, "whisparrot", settings.LoadResult.Describe(), System.Windows.Forms.ToolTipIcon.Error);
                return;
            }

            // 採用キーは保存しない（設定のキーは設定ウィンドウでの明示保存だけが変える）。
            var adopted = hotkey.Register(new SettingsHotkeyStore(settings).NegotiationOrder());
            UpdateTrayText();
            foreach (var (display, ok, error) in hotkey.Attempts)
                Console.WriteLine($"hotkey {display}: {(ok ? "OK" : $"FAILED win32_error={error}")}");
            Console.WriteLine($"hotkey_adopted={adopted?.Display ?? "(none)"}");
            if (adopted is null)
                tray.ShowBalloonTip(4000, "whisparrot", "ホットキーを登録できませんでした。トレイのメニューから開始/停止できます。", System.Windows.Forms.ToolTipIcon.Warning);

            try { autostart.Apply(settings.Current.Autostart); } // exe を移動した後の修復を含む
            catch (Exception ex) { Console.WriteLine("autostart_sync_failed=" + ex.GetType().Name); }

            // 初回起動の導線: 選択中のモデルが使えなければ設定を開く（検証は背景で行い、キャッシュも温める）。
            _ = Task.Run(async () =>
            {
                var id = settings.Current.Recognition.Model;
                var st = await models.GetStatusAsync(id);
                Console.WriteLine($"model_status {id}={st.State}");
                if (st.State != ModelState.Ready)
                    await app.Dispatcher.BeginInvoke(() => OpenSettings($"モデル「{id}」が使えません（{(st.State == ModelState.NotDownloaded ? "未取得" : "壊れています")}）。「ローカル認識」で取得してください。"));
            });
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
}
