using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Whisparrot.Core;
using Whisparrot.Core.Settings;
using Whisparrot.Infrastructure.Audio;
using Whisparrot.Infrastructure.Models;
using Whisparrot.Infrastructure.Network;
using Whisparrot.Infrastructure.Settings;

namespace Whisparrot.App.Views;

/// <summary>
/// 設計 §6.2 の項目を操作できる最小限の設定ウィンドウ（見た目・アクセシビリティの完成は T14）。
/// 保存は「ホットキー登録検査 → 自動起動 → config.json」の順に行い、後段の失敗では前段を戻す。
/// </summary>
internal sealed class SettingsWindow : Window
{
    private readonly SettingsService _settings;
    private readonly ISecretProtector _protector;
    private readonly ModelManager _models;
    private readonly NetworkContext _network;
    private readonly AutostartService _autostart;
    private readonly Func<Hotkey, (bool Ok, int Error)> _tryHotkey;
    private readonly Func<Hotkey?> _adoptedHotkey;
    private readonly Func<string?> _backendText;

    private readonly CheckBox _ctrl = new() { Content = "Ctrl" }, _alt = new() { Content = "Alt" }, _shift = new() { Content = "Shift" }, _win = new() { Content = "Win" };
    private readonly ComboBox _key = new() { Width = 90 };
    private readonly ComboBox _mic = new() { MinWidth = 280 };
    private readonly RadioButton _insClipboard = new() { Content = "クリップボード（Ctrl+V）", GroupName = "ins" };
    private readonly RadioButton _insTyping = new() { Content = "タイピング（SendInput）— 未実装（T14）", GroupName = "ins", IsEnabled = false };

    private readonly Dictionary<string, RadioButton> _modelRadios = [];
    private readonly Dictionary<string, TextBlock> _modelStatus = [];
    private readonly Dictionary<string, Button> _modelButtons = [];
    private readonly ProgressBar _progress = new() { Height = 10, Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed };
    private readonly TextBlock _backend = new();
    private CancellationTokenSource? _download;

    private readonly RadioButton _cleanOff = new() { Content = "OFF", GroupName = "clean" };
    private readonly RadioButton _cleanCompat = new() { Content = "OpenAI 互換エンドポイント", GroupName = "clean" };
    private readonly RadioButton _cleanChatGpt = new() { Content = "ChatGPT — 未対応（T13）", GroupName = "clean", IsEnabled = false };
    private readonly TextBox _baseUrl = new(), _cleanModel = new();
    private readonly PasswordBox _apiKey = new();
    private readonly CheckBox _clearKey = new() { Content = "保存済みの API キーを削除" };
    private readonly TextBlock _destHost = new() { Foreground = System.Windows.Media.Brushes.DimGray };

    private readonly RadioButton _pxEnv = new() { Content = "環境変数に従う", GroupName = "px" };
    private readonly RadioButton _pxManual = new() { Content = "手動指定", GroupName = "px" };
    private readonly RadioButton _pxNone = new() { Content = "使わない", GroupName = "px" };
    private readonly TextBox _pxUrl = new(), _pxUser = new();
    private readonly PasswordBox _pxPass = new();
    private readonly CheckBox _clearPxPass = new() { Content = "保存済みのパスワードを削除" };

    private readonly CheckBox _autostartBox = new() { Content = "ログオン時に自動起動する" };
    private readonly TextBlock _error = new() { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };

    public SettingsWindow(SettingsService settings, ISecretProtector protector, ModelManager models, NetworkContext network,
        AutostartService autostart, Func<Hotkey, (bool Ok, int Error)> tryHotkey, Func<Hotkey?> adoptedHotkey, Func<string?> backendText, string? notice)
    {
        _settings = settings; _protector = protector; _models = models; _network = network;
        _autostart = autostart; _tryHotkey = tryHotkey; _adoptedHotkey = adoptedHotkey; _backendText = backendText;

        Title = "whisparrot 設定";
        Width = 520; Height = 700; MinWidth = 440;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var root = new DockPanel { Margin = new Thickness(12) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var save = new Button { Content = "保存", Width = 80, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "キャンセル", Width = 90, IsCancel = true };
        save.Click += (_, _) => Save();
        buttons.Children.Add(save); buttons.Children.Add(cancel);
        var bottom = new StackPanel();
        bottom.Children.Add(_error); bottom.Children.Add(buttons);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(bottom);

        var body = new StackPanel();
        if (notice is not null) body.Children.Add(new TextBlock { Text = notice, TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.DarkOrange, Margin = new Thickness(0, 0, 0, 8) });
        if (!settings.LoadResult.Usable)
            body.Children.Add(new TextBlock { Text = settings.LoadResult.Describe(), TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.Firebrick, Margin = new Thickness(0, 0, 0, 8) });
        body.Children.Add(BuildInput());
        body.Children.Add(BuildRecognition());
        body.Children.Add(BuildCleanup());
        body.Children.Add(BuildNetwork());
        body.Children.Add(BuildStartup());
        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body });
        Content = root;

        Load(settings.Current);
        Loaded += async (_, _) => await RefreshModelStatusAsync();
        Closing += (_, e) =>
        {
            if (_download is null) return;
            if (MessageBox.Show(this, "モデルの取得中です。中止して閉じますか？", "whisparrot", MessageBoxButton.YesNo) == MessageBoxResult.Yes) _download.Cancel();
            else e.Cancel = true;
        };
    }

    private static GroupBox Group(string title, params UIElement[] children)
    {
        var p = new StackPanel { Margin = new Thickness(6) };
        foreach (var c in children) { if (c is FrameworkElement fe && fe.Margin == default) fe.Margin = new Thickness(0, 2, 0, 2); p.Children.Add(c); }
        return new GroupBox { Header = title, Content = p, Margin = new Thickness(0, 0, 0, 10) };
    }

    private static UIElement Row(string label, UIElement field)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var t = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(field, 1);
        g.Children.Add(t); g.Children.Add(field);
        return g;
    }

    private UIElement BuildInput()
    {
        foreach (var k in KeyChoices()) _key.Items.Add(k);
        var hk = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var c in new[] { _ctrl, _alt, _shift, _win }) { c.Margin = new Thickness(0, 0, 10, 0); c.VerticalAlignment = VerticalAlignment.Center; hk.Children.Add(c); }
        hk.Children.Add(new TextBlock { Text = "＋ ", VerticalAlignment = VerticalAlignment.Center });
        hk.Children.Add(_key);
        _mic.DisplayMemberPath = nameof(MicChoice.Name);
        return Group("入力", Row("ホットキー", hk), Row("マイク", _mic), new TextBlock { Text = "投入方式" }, _insClipboard, _insTyping);
    }

    private static IEnumerable<string> KeyChoices()
    {
        for (var c = 'A'; c <= 'Z'; c++) yield return c.ToString();
        for (var c = '0'; c <= '9'; c++) yield return c.ToString();
        for (var i = 1; i <= 12; i++) yield return "F" + i;
        foreach (var n in new[] { "Space", "Insert", "Delete", "Home", "End", "PageUp", "PageDown", "Left", "Right", "Up", "Down" }) yield return n;
    }

    private sealed record MicChoice(string? Id, string Name);

    private UIElement BuildRecognition()
    {
        var items = new List<UIElement>();
        foreach (var id in ModelIds.All)
        {
            var e = _models.Catalog.Get(id);
            var radio = new RadioButton { Content = $"{id}（{e.SizeBytes / (1024.0 * 1024):N0} MiB）", GroupName = "model", MinWidth = 190, VerticalAlignment = VerticalAlignment.Center };
            var status = new TextBlock { Text = "確認中…", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0), MinWidth = 90 };
            var btn = new Button { Content = "取得", Padding = new Thickness(10, 1, 10, 1), IsEnabled = false };
            var capturedId = id;
            btn.Click += async (_, _) => await OnModelButton(capturedId);
            _modelRadios[id] = radio; _modelStatus[id] = status; _modelButtons[id] = btn;
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(radio); row.Children.Add(status); row.Children.Add(btn);
            items.Add(row);
        }
        items.Add(_progress);
        items.Add(new TextBlock { Text = "モデルはこのアプリの models フォルダへ保存され、SHA-256 を照合して使われます。", Foreground = System.Windows.Media.Brushes.DimGray, TextWrapping = TextWrapping.Wrap });
        items.Add(_backend);
        return Group("ローカル認識", [.. items]);
    }

    private UIElement BuildCleanup()
    {
        _apiKey.ToolTip = "保存済みの場合は空のままにすると維持されます";
        _baseUrl.TextChanged += (_, _) => UpdateDest();
        return Group("整文", _cleanOff, _cleanCompat, _cleanChatGpt,
            Row("ベース URL", _baseUrl), _destHost, Row("API キー", _apiKey), _clearKey, Row("モデル名", _cleanModel),
            new TextBlock { Text = "「互換エンドポイント」を選ぶと、認識したテキストだけをここで指定した送信先へ送って整文します（失敗・未設定・時間切れのときは認識結果をそのまま入力）。API キーは Windows のユーザー保護（DPAPI）で暗号化して保存します。ChatGPT 経由は未対応です。", TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.DimGray });
    }

    private UIElement BuildNetwork()
    {
        var env = _network.EnvironmentProxyDisplay();
        var lines = new List<string> { env is null ? "環境変数のプロキシ: なし（直結）" : $"環境変数のプロキシ: {env}" };
        lines.AddRange(_network.Environment.Conflicts);
        return Group("通信", _pxEnv, new TextBlock { Text = string.Join("\n", lines), Foreground = System.Windows.Media.Brushes.DimGray, Margin = new Thickness(20, 0, 0, 4) },
            _pxManual, Row("URL", _pxUrl), Row("ユーザー名", _pxUser), Row("パスワード", _pxPass), _clearPxPass, _pxNone,
            new TextBlock { Text = "手動指定は http://host:port または https://host:port の形式（https:// はプロキシへの接続を TLS で行い、証明書は OS の信頼ストアで検証します）。", Foreground = System.Windows.Media.Brushes.DimGray });
    }

    private UIElement BuildStartup() => Group("起動・更新", _autostartBox,
        new TextBlock { Text = $"バージョン {AppInfo.Version} / {RuntimeInformation.ProcessArchitecture}" },
        new Button { Content = "更新を確認（今後の版で有効）", IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 1, 10, 1) });

    private void UpdateDest()
    {
        _destHost.Text = CompatibleEndpointUrl.Validate(_baseUrl.Text.Trim(), out var u) is null && u is not null
            ? $"送信先ホスト: {CompatibleEndpointUrl.DestinationHost(u)}" : "";
    }

    // ---- 読み込み ----

    private void Load(AppSettings s)
    {
        _ctrl.IsChecked = s.Hotkey.Modifiers.HasFlag(HotkeyModifiers.Control);
        _alt.IsChecked = s.Hotkey.Modifiers.HasFlag(HotkeyModifiers.Alt);
        _shift.IsChecked = s.Hotkey.Modifiers.HasFlag(HotkeyModifiers.Shift);
        _win.IsChecked = s.Hotkey.Modifiers.HasFlag(HotkeyModifiers.Win);
        _key.SelectedItem = HotkeyFormat.KeyName(s.Hotkey.VirtualKey);

        _mic.Items.Add(new MicChoice(null, "既定の通信デバイス"));
        foreach (var m in MicrophoneList.Enumerate()) _mic.Items.Add(new MicChoice(m.Id, m.Name));
        if (s.MicrophoneId is not null && !_mic.Items.Cast<MicChoice>().Any(c => c.Id == s.MicrophoneId))
            _mic.Items.Add(new MicChoice(s.MicrophoneId, "（見つかりません）" + s.MicrophoneId));
        _mic.SelectedItem = _mic.Items.Cast<MicChoice>().First(c => c.Id == s.MicrophoneId);

        _insClipboard.IsChecked = true;
        _modelRadios[s.Recognition.Model].IsChecked = true;

        _cleanOff.IsChecked = s.Cleanup.Provider == CleanupProvider.Off;
        _cleanCompat.IsChecked = s.Cleanup.Provider == CleanupProvider.Compatible;
        _cleanChatGpt.IsChecked = s.Cleanup.Provider == CleanupProvider.ChatGpt;
        _baseUrl.Text = s.Cleanup.Compatible.BaseUrl;
        _cleanModel.Text = s.Cleanup.Compatible.Model;
        _clearKey.IsEnabled = s.Cleanup.Compatible.ApiKeyDpapi is not null;
        UpdateDest();

        _pxEnv.IsChecked = s.Proxy.Mode == ProxyMode.Environment;
        _pxManual.IsChecked = s.Proxy.Mode == ProxyMode.Manual;
        _pxNone.IsChecked = s.Proxy.Mode == ProxyMode.None;
        _pxUrl.Text = s.Proxy.ManualUrl;
        _pxUser.Text = s.Proxy.ManualUsername;
        _clearPxPass.IsEnabled = s.Proxy.ManualPasswordDpapi is not null;

        _autostartBox.IsChecked = s.Autostart;
        _backend.Text = "実際の実行方式: " + (_backendText() ?? "未実行");
    }

    // ---- モデル ----

    private async Task RefreshModelStatusAsync()
    {
        foreach (var id in ModelIds.All)
        {
            _modelStatus[id].Text = "確認中…";
            try
            {
                var st = await _models.GetStatusAsync(id);
                SetModelRow(id, st);
            }
            catch (Exception ex) { _modelStatus[id].Text = "確認できません"; _modelStatus[id].ToolTip = ex.Message; }
        }
    }

    private void SetModelRow(string id, ModelStatus st)
    {
        _modelStatus[id].Text = st.State switch
        {
            ModelState.Ready => "取得済み（検証済み）",
            ModelState.NotDownloaded => "未取得",
            ModelState.Corrupt => "壊れています",
            _ => "取得中",
        };
        _modelStatus[id].ToolTip = st.Detail;
        var downloading = _download is not null;
        _modelButtons[id].Content = downloading && st.State == ModelState.Downloading ? "取消" : st.State == ModelState.Corrupt ? "再取得" : "取得";
        _modelButtons[id].IsEnabled = st.State == ModelState.Downloading || (!downloading && st.State != ModelState.Ready);
    }

    private async Task OnModelButton(string id)
    {
        if (_download is not null) { _download.Cancel(); return; }
        _error.Text = "";
        _download = new CancellationTokenSource();
        _progress.Visibility = Visibility.Visible; _progress.Value = 0;
        _modelStatus[id].Text = "取得中 0%";
        _modelButtons[id].Content = "取消"; _modelButtons[id].IsEnabled = true;
        foreach (var other in ModelIds.All.Where(o => o != id)) _modelButtons[other].IsEnabled = false;
        var progress = new Progress<DownloadProgress>(p =>
        {
            var pct = p.BytesDone * 100.0 / Math.Max(1, p.BytesTotal);
            _progress.Value = pct;
            _modelStatus[id].Text = $"取得中 {pct:N0}%（{p.BytesDone / (1024 * 1024):N0} / {p.BytesTotal / (1024 * 1024):N0} MiB）";
        });
        try
        {
            await Task.Run(() => _models.DownloadAsync(id, progress, _download.Token));
        }
        catch (OperationCanceledException) { _error.Text = "取得を中止しました。モデルは配置していません。"; }
        catch (Exception ex) { _error.Text = $"モデルを取得できませんでした: {ex.Message}"; }
        finally
        {
            _download.Dispose(); _download = null;
            _progress.Visibility = Visibility.Collapsed;
        }
        await RefreshModelStatusAsync();
    }

    // ---- 保存 ----

    private void Save()
    {
        _error.Text = "";
        var cur = _settings.Current;
        var mods = HotkeyModifiers.None;
        if (_ctrl.IsChecked == true) mods |= HotkeyModifiers.Control;
        if (_alt.IsChecked == true) mods |= HotkeyModifiers.Alt;
        if (_shift.IsChecked == true) mods |= HotkeyModifiers.Shift;
        if (_win.IsChecked == true) mods |= HotkeyModifiers.Win;
        if (_key.SelectedItem is not string keyName || !HotkeyFormat.TryParseKey(keyName, out var vk) || mods == HotkeyModifiers.None)
        { _error.Text = "ホットキーは修飾キー 1 つ以上とキーを選んでください。"; return; }
        var hotkey = HotkeyFormat.Create(mods, vk);

        string? apiKey = cur.Cleanup.Compatible.ApiKeyDpapi;
        if (_clearKey.IsChecked == true) apiKey = null;
        else if (_apiKey.Password.Length > 0) apiKey = _protector.Protect(_apiKey.Password);
        string? pxPass = cur.Proxy.ManualPasswordDpapi;
        if (_clearPxPass.IsChecked == true) pxPass = null;
        else if (_pxPass.Password.Length > 0) pxPass = _protector.Protect(_pxPass.Password);

        var next = cur with
        {
            Hotkey = hotkey,
            MicrophoneId = (_mic.SelectedItem as MicChoice)?.Id,
            Recognition = cur.Recognition with { Model = _modelRadios.First(kv => kv.Value.IsChecked == true).Key },
            Cleanup = cur.Cleanup with
            {
                Provider = _cleanCompat.IsChecked == true ? CleanupProvider.Compatible : _cleanChatGpt.IsChecked == true ? CleanupProvider.ChatGpt : CleanupProvider.Off,
                Compatible = new CompatibleCleanupSettings(_baseUrl.Text.Trim(), apiKey, _cleanModel.Text.Trim()),
            },
            Proxy = new ProxySettings(_pxManual.IsChecked == true ? ProxyMode.Manual : _pxNone.IsChecked == true ? ProxyMode.None : ProxyMode.Environment,
                _pxUrl.Text.Trim(), _pxUser.Text.Trim(), pxPass),
            Autostart = _autostartBox.IsChecked == true,
            Insertion = new InsertionSettings(InsertionMethod.Clipboard),
        };

        var issues = SettingsValidator.Validate(next, DateTimeOffset.UtcNow);
        if (issues.Count > 0) { _error.Text = string.Join("\n", issues); return; }
        if (!_settings.IsWritable) { _error.Text = "config.json を読み込めなかったため、保存できません。" + _settings.LoadResult.Describe(); return; }

        // 設定のキーが変わったときだけ登録を試す。巻き戻しは「設定のキー」ではなく直前に実際に有効だった（採用済みの）キーへ。
        var hotkeyChanged = !HotkeyFormat.SameKey(hotkey, cur.Hotkey);
        var previouslyActive = _adoptedHotkey();
        void RestoreHotkey() { if (hotkeyChanged && previouslyActive is not null) _tryHotkey(previouslyActive); }
        if (hotkeyChanged)
        {
            var (ok, err) = _tryHotkey(hotkey);
            if (!ok) { _error.Text = $"ホットキー {hotkey.Display} を登録できません（Win32 エラー {err}）。他のアプリが使用中か、OS が予約している可能性があります。"; return; }
        }
        var autostartChanged = next.Autostart != cur.Autostart || next.Autostart;
        try
        {
            if (autostartChanged) _autostart.Apply(next.Autostart);
        }
        catch (Exception ex)
        {
            RestoreHotkey();
            _error.Text = $"自動起動の設定に失敗しました（保存していません）: {ex.Message}";
            return;
        }
        try
        {
            _settings.Save(next);
        }
        catch (Exception ex)
        {
            RestoreHotkey();
            try { if (next.Autostart != cur.Autostart) _autostart.Apply(cur.Autostart); } catch { }
            _error.Text = $"保存に失敗しました: {ex.Message}";
            return;
        }
        Close();
    }
}
