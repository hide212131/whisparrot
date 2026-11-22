using System.Windows;
using Whisparrot.App.Views;
using Whisparrot.App.Windows;
using Whisparrot.Core;
using Whisparrot.Core.Settings;
using Whisparrot.Infrastructure;
using Whisparrot.Infrastructure.Audio;
using Whisparrot.Infrastructure.ChatGpt;
using Whisparrot.Infrastructure.Hane;
using Whisparrot.Infrastructure.Models;
using Whisparrot.Infrastructure.Network;
using Whisparrot.Infrastructure.Polish;
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
        var injector = new TextInjector(() => settings.Current.Insertion);
        var chatGpt = new ChatGptAuthService(network, new ChatGptTokenStore(AppPaths.ChatGptTokenPath, protector));
        var polisher = new SettingsTextPolisher(() => settings.Current, protector, network, chatGpt);
        // 認識の選択（local / cloud）は録音開始時に固定する。ローカルの準備確認・ワーカーは既存の WhisperRecognizer のまま。
        var recognition = new SettingsRecognizer(() => settings.Current, protector, network, recognizer);
        // Hane 連携（設計 §4.6）。既定 OFF。ON のときだけ、録音開始時に前景の Hane へ文脈を取りにいく。
        var haneLink = new SettingsHaneLink(() => settings.Current, new HaneClient());
        var coordinator = new SessionCoordinator(recorder, recognition, injector, polisher, haneLink);

        var pill = new PillWindow();
        var hotkey = new HotkeyService();
        using var tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = $"whisparrot {AppInfo.DisplayVersion}",
            Visible = true,
        };

        // 直近のセッションの認識先（設定画面の「今回の認識」）。ローカルの実行方式欄とは別に持つ。
        string? lastRecognition = null;
        coordinator.BackendReported += b => lastRecognition = b == "cloud" ? "クラウド" : $"ローカル（{b}）";

        SettingsWindow? settingsWindow = null;
        void OpenSettings(string? notice = null)
        {
            if (settingsWindow is not null) { settingsWindow.Activate(); return; }
            settingsWindow = new SettingsWindow(settings, protector, models, network, chatGpt, autostart,
                hk => (hotkey.TryReplace(hk, out var err), err), () => hotkey.Adopted, () => recognizer.LastBackend, notice, () => lastRecognition);
            settingsWindow.Closed += (_, _) => settingsWindow = null;
            settingsWindow.Show();
            settingsWindow.Activate();
        }

        System.Windows.Forms.ToolStripItem engineItem = null!;
        void UpdateTrayText()
        {
            var hk = hotkey.Adopted;
            var name = $"whisparrot {AppInfo.DisplayVersion}";
            var cloud = settings.Current.Recognition.Engine == RecognitionEngine.Cloud;
            tray.Text = !settings.LoadResult.Usable ? $"{name}（設定エラー）"
                : hk is null ? $"{name}（ホットキー未登録: トレイから操作）" : $"{name}（{hk.Display}{(cloud ? "・クラウド認識" : "")}）";
            // 音声の送信先を、設定を開かなくても確認できるようにする（設計 §9.1）。
            var c = settings.Current.Recognition.Cloud;
            var host = CompatibleEndpointUrl.Validate(c.BaseUrl, out var u) is null && u is not null ? CompatibleEndpointUrl.DestinationHost(u) : "接続先未設定";
            engineItem.Text = cloud ? $"認識: クラウド（音声の送信先 {host}）" : "認識: ローカル（音声は送信しません）";
        }

        var menu = new System.Windows.Forms.ContextMenuStrip();
        engineItem = menu.Items.Add("認識");
        engineItem.Enabled = false;
        menu.Items.Add("設定", null, (_, _) => OpenSettings());
        var toggleItem = menu.Items.Add("開始 / 停止", null, (_, _) => coordinator.Toggle());
        var cancelItem = menu.Items.Add("取消", null, (_, _) => coordinator.Cancel());
        var retryItem = menu.Items.Add("入力を再試行", null, (_, _) => coordinator.RetryInsert());
        retryItem.Visible = false;
        // 確定できなかった一件（Hane への挿入の結果不明・差し替えの不成立）。コピーか破棄だけを提供し、自動では入力しない（設計 §4.5）。
        var copyHeldItem = menu.Items.Add("保持した文をコピー", null, (_, _) =>
        {
            if (coordinator.Held is not { } held) return;
            try { System.Windows.Clipboard.SetText(held.Text); coordinator.DiscardHeld(); }
            catch (Exception ex) { tray.ShowBalloonTip(3000, "whisparrot", $"コピーできませんでした（{ex.GetType().Name}）", System.Windows.Forms.ToolTipIcon.Warning); }
        });
        var discardHeldItem = menu.Items.Add("保持した文を破棄", null, (_, _) => coordinator.DiscardHeld());
        copyHeldItem.Visible = discardHeldItem.Visible = false;
        coordinator.HeldChanged += held => app.Dispatcher.BeginInvoke(() =>
        {
            copyHeldItem.Visible = discardHeldItem.Visible = held is not null;
            if (held is not null)
                copyHeldItem.Text = held.Kind == HeldKind.PolishedNotApplied ? "整文結果をコピー" : "入力を確認できなかった文をコピー";
        });
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
            pill.ShowState(s.State, s.Engine);
            retryItem.Visible = s.State == SessionState.Failed;
        });
        // 失敗の通知は短く（3 秒以内）。クラウド認識の失敗は、通知をクリックして設定を開き原因を確認できる。
        var balloonOpensSettings = false;
        coordinator.Noticed += n => app.Dispatcher.BeginInvoke(() =>
        {
            balloonOpensSettings = n.Kind == NoticeKind.Error && settings.Current.Recognition.Engine == RecognitionEngine.Cloud;
            tray.ShowBalloonTip(2500, "whisparrot", balloonOpensSettings ? n.Message + "（クリックで設定を開く）" : n.Message,
                n.Kind is NoticeKind.Error or NoticeKind.Warning ? System.Windows.Forms.ToolTipIcon.Warning : System.Windows.Forms.ToolTipIcon.Info);
        });
        tray.BalloonTipClicked += (_, _) => { if (balloonOpensSettings) { balloonOpensSettings = false; OpenSettings(); } };
        hotkey.Pressed += () => coordinator.Toggle();

        // 適用範囲（設計 §3.2）: ホットキー・自動起動は設定画面の保存時に即時。モデル・マイクは次のセッションから。
        // プロキシは次の通信（クライアント生成）から。ここでは待機中の認識子だけを解放する。
        settings.Changed += (old, next) => app.Dispatcher.BeginInvoke(() =>
        {
            if (!old.Hane.Enabled && next.Hane.Enabled) _ = Task.Run(HaneClient.WarmUp);
            if (old.Recognition.Model != next.Recognition.Model
                || (old.Recognition.Engine != next.Recognition.Engine && next.Recognition.Engine == RecognitionEngine.Cloud))
                recognizer.ReleaseIdleWorker();
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

            if (settings.Current.Hane.Enabled) _ = Task.Run(HaneClient.WarmUp); // 初回の文脈取得を予算内に収める
            try { autostart.Apply(settings.Current.Autostart); } // exe を移動した後の修復を含む
            catch (Exception ex) { Console.WriteLine("autostart_sync_failed=" + ex.GetType().Name); }

            // 初回起動の導線: ローカル選択中にモデルが使えなければ設定を開く（検証は背景で行い、キャッシュも温める）。
            // クラウド選択時はローカルモデルを要求しない（案内も検証もしない）。
            if (settings.Current.Recognition.Engine == RecognitionEngine.Cloud)
            {
                Console.WriteLine("recognition_engine=cloud");
                return;
            }
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
