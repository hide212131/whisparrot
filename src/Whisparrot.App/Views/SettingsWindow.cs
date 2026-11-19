using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Whisparrot.Core;
using Whisparrot.Core.Settings;
using Whisparrot.Infrastructure.Audio;
using Whisparrot.Infrastructure.ChatGpt;
using Whisparrot.Infrastructure.Models;
using Whisparrot.Infrastructure.Network;
using Whisparrot.Infrastructure.Recognition;
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
    private readonly ChatGptAuthService _chatGpt;
    private readonly AutostartService _autostart;
    private readonly Func<Hotkey, (bool Ok, int Error)> _tryHotkey;
    private readonly Func<Hotkey?> _adoptedHotkey;
    private readonly Func<string?> _backendText;
    private readonly Func<string?>? _lastRecognitionText;

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

    private readonly RadioButton _engLocal = new() { Content = "ローカル（この PC で認識。音声は送信しません）", GroupName = "eng" };
    private readonly RadioButton _engCloud = new() { Content = "クラウド（OpenAI 互換の文字起こし API へ音声を送信）", GroupName = "eng" };
    private readonly StackPanel _localPanel = new() { Margin = new Thickness(20, 0, 0, 4) };
    private readonly StackPanel _cloudPanel = new() { Margin = new Thickness(20, 0, 0, 4) };
    private readonly TextBox _cUrl = new(), _cModel = new(), _cLang = new() { MaxLength = 16, Width = 80, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly PasswordBox _cKey = new();
    private readonly CheckBox _cClearKey = new() { Content = "保存済みの API キーを削除" };
    private readonly TextBlock _cTarget = new() { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.DimGray };
    private readonly TextBlock _cKeyState = new() { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.DimGray };
    private readonly TextBlock _cStatus = new() { TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.DarkOrange };
    private readonly TextBlock _lastRecognition = new();
    private string? _cCopiedKey;
    private bool _cCopied;

    private readonly RadioButton _cleanOff = new() { Content = "OFF", GroupName = "clean" };
    private readonly RadioButton _cleanCompat = new() { Content = "OpenAI 互換エンドポイント", GroupName = "clean" };
    private readonly RadioButton _cleanChatGpt = new() { Content = "ChatGPT（Sign in with ChatGPT）", GroupName = "clean" };
    private readonly StackPanel _cgPanel = new() { Margin = new Thickness(20, 0, 0, 4) };
    private readonly TextBlock _cgStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _cgLogin = new() { Content = "ログイン", Padding = new Thickness(10, 1, 10, 1), Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _cgLogout = new() { Content = "ログアウト", Padding = new Thickness(10, 1, 10, 1) };
    private readonly TextBox _cgModel = new();
    private readonly StackPanel _cgPaste = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBox _cgAuthUrl = new() { IsReadOnly = true };
    private readonly TextBox _cgPasted = new();
    private ChatGptAuthService.ChatGptLoginAttempt? _login;
    private readonly TextBox _baseUrl = new(), _cleanModel = new();
    private readonly PasswordBox _apiKey = new();
    private readonly CheckBox _clearKey = new() { Content = "保存済みの API キーを削除" };
    private readonly TextBlock _destHost = new() { Foreground = System.Windows.Media.Brushes.DimGray };

    private readonly RadioButton _pxEnv = new() { Content = "環境変数に従う", GroupName = "px" };
    private readonly RadioButton _pxManual = new() { Content = "手動指定", GroupName = "px" };
    private readonly RadioButton _pxNone = new() { Content = "使わない", GroupName = "px" };
    private readonly RadioButton _pxAuthCred = new() { Content = "ユーザー名とパスワード", GroupName = "pxauth" };
    private readonly RadioButton _pxAuthWindows = new() { Content = "Windows のログオン情報を使う", GroupName = "pxauth" };
    private readonly TextBox _pxUrl = new(), _pxUser = new();
    private readonly PasswordBox _pxPass = new();
    private readonly CheckBox _clearPxPass = new() { Content = "保存済みのパスワードを削除" };

    private readonly CheckBox _autostartBox = new() { Content = "ログオン時に自動起動する" };
    private readonly TextBlock _error = new() { Foreground = System.Windows.Media.Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };

    public SettingsWindow(SettingsService settings, ISecretProtector protector, ModelManager models, NetworkContext network, ChatGptAuthService chatGpt,
        AutostartService autostart, Func<Hotkey, (bool Ok, int Error)> tryHotkey, Func<Hotkey?> adoptedHotkey, Func<string?> backendText, string? notice, Func<string?>? lastRecognitionText = null)
    {
        _settings = settings; _protector = protector; _models = models; _network = network; _chatGpt = chatGpt;
        _autostart = autostart; _tryHotkey = tryHotkey; _adoptedHotkey = adoptedHotkey; _backendText = backendText; _lastRecognitionText = lastRecognitionText;

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
        Closed += (_, _) => _login?.Dispose();
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
        foreach (var i in items) _localPanel.Children.Add(i);

        _engLocal.Checked += (_, _) => UpdateEngineVisibility();
        _engCloud.Checked += (_, _) => UpdateEngineVisibility();
        _cUrl.TextChanged += (_, _) => { UpdateCloudTarget(); UpdateCloudStatus(); };
        _cModel.TextChanged += (_, _) => UpdateCloudStatus();
        _cLang.TextChanged += (_, _) => UpdateCloudStatus();
        _cKey.PasswordChanged += (_, _) =>
        {
            if (_cKey.Password.Length > 0) _cClearKey.IsChecked = false;
            UpdateCloudStatus();
        };
        _cClearKey.Checked += (_, _) => UpdateCloudStatus();
        _cClearKey.Unchecked += (_, _) => UpdateCloudStatus();
        var copy = new Button { Content = "整文の互換接続からコピー", Padding = new Thickness(10, 1, 10, 1), Margin = new Thickness(0, 0, 8, 0) };
        var clear = new Button { Content = "接続設定を消去", Padding = new Thickness(10, 1, 10, 1) };
        copy.Click += (_, _) => OnCopyFromCleanup();
        clear.Click += (_, _) => ClearCloudConnection();
        var cloudButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        cloudButtons.Children.Add(copy); cloudButtons.Children.Add(clear);
        _cKey.ToolTip = "保存済みの場合は空のままにすると維持されます";
        System.Windows.Automation.AutomationProperties.SetName(_cUrl, "クラウド認識のベース URL");
        System.Windows.Automation.AutomationProperties.SetName(_cKey, "クラウド認識の API キー");
        System.Windows.Automation.AutomationProperties.SetName(_cModel, "クラウド認識のモデル名");
        System.Windows.Automation.AutomationProperties.SetName(_cLang, "クラウド認識の言語コード");
        foreach (var e in new UIElement[]
        {
            Row("ベース URL", _cUrl),
            new TextBlock { Text = "例: https://transcription.example.com/v1（/v1 などの API root まで含めて入力。末尾に /audio/transcriptions が加わります）", TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.DimGray },
            _cTarget,
            Row("API キー", _cKey), _cKeyState, _cClearKey,
            Row("モデル名", _cModel),
            new TextBlock { Text = "例: whisper（接続先の公開名をそのまま入力します。名前の変換や存在確認はしません）", TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.DimGray },
            Row("言語コード", _cLang),
            new TextBlock { Text = "例: ja（既定）、en。空欄にすると言語を指定せず、接続先の自動判定に任せます（日本語が他言語と判定されやすくなります）", TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.DimGray },
            cloudButtons,
            new TextBlock { Text = "コピーは URL と API キーだけを一度複写します（整文のモデルは複写せず、以後は連動しません。入力済みの内容は上書きされます）。保存するまで反映されません。", TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.DimGray },
            _cStatus,
        }) _cloudPanel.Children.Add(e);

        var notice = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "クラウド認識を選ぶと、録音停止後に音声を指定した接続先へ送信します。接続先が LiteLLM 等のプロキシの場合、その先の文字起こしサービスへ転送される場合があります。整文 OFF でも音声は送信されます。ローカル認識では音声を送信しません。\n\n"
                 + "短い発話向けです。音声ファイルの上限は 25 MB（約 13 分）、処理の期限は 60 秒です。文字起こしに失敗した音声は保存されず、もう一度話す必要があります。\n\n"
                 + "接続先と転送先での保存・ログの扱いは、そのサービスの設定を確認してください。",
        };
        System.Windows.Automation.AutomationProperties.SetName(notice, "クラウド認識の音声送信に関する説明");
        return Group("認識", _engLocal, _localPanel, _engCloud, _cloudPanel, notice, _lastRecognition);
    }

    private void UpdateEngineVisibility()
    {
        _localPanel.Visibility = _engLocal.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        _cloudPanel.Visibility = _engCloud.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        UpdateCloudTarget();
        UpdateCloudStatus();
    }

    private void UpdateCloudTarget()
    {
        var target = CloudConnectionForm.RequestTarget(_cUrl.Text.Trim());
        _cTarget.Text = target is null ? "" : $"実際の要求先: {target}（音声の送信先ホスト: {CompatibleEndpointUrl.DestinationHost(new Uri(target))}）";
    }

    /// <summary>保存済み / 入力 / 削除 / コピーのうち、保存時に使うキーの暗号文。優先順は 削除 → 入力 → コピー → 保存済み。</summary>
    private string? ResolveCloudKey()
    {
        if (_cClearKey.IsChecked == true) return null;
        if (_cKey.Password.Length > 0) return _protector.Protect(_cKey.Password);
        if (_cCopied) return _cCopiedKey;
        return _settings.Current.Recognition.Cloud.ApiKeyDpapi;
    }

    private CloudRecognitionSettings ReadCloudForm() => new(_cUrl.Text.Trim(), ResolveCloudKey(), _cModel.Text.Trim(), _cLang.Text.Trim());

    /// <summary>クラウドが選ばれているとき、今の入力のままでは録音を始められない理由を示す（ネットワークには触れない）。</summary>
    private void UpdateCloudStatus()
    {
        if (_engCloud.IsChecked != true) { _cStatus.Text = ""; return; }
        var saved = _settings.Current.Recognition.Cloud.ApiKeyDpapi is not null;
        _cKeyState.Text = _cClearKey.IsChecked == true ? "API キー: 保存時に削除されます"
            : _cKey.Password.Length > 0 ? "API キー: 入力済み（保存時に暗号化します）"
            : _cCopied ? (_cCopiedKey is null ? "API キー: コピー元に保存がないため未設定になります" : "API キー: 整文の接続からコピー済み（保存時に反映）")
            : saved ? "API キー: 保存済み（空のままなら維持されます）" : "API キー: 未設定";
        RefreshClearKeyEnabled();
        var reason = CloudConnectionForm.NotReadyReason(_settings.Current.Recognition with { Cloud = ReadCloudForm() }, _protector);
        _cStatus.Text = reason is null ? "" : $"このままではクラウド認識を使えません: {reason}";
    }

    private void RefreshClearKeyEnabled() =>
        _cClearKey.IsEnabled = _settings.Current.Recognition.Cloud.ApiKeyDpapi is not null || _cCopied;

    /// <summary>保存済みの整文の互換接続から URL / キーを複写する（画面の入力だけを変え、保存はしない）。</summary>
    private void OnCopyFromCleanup()
    {
        var hasInput = _cUrl.Text.Trim().Length > 0 || _cKey.Password.Length > 0 || _cCopied || _settings.Current.Recognition.Cloud.ApiKeyDpapi is not null && _cClearKey.IsChecked != true;
        if (hasInput && MessageBox.Show(this, "現在のベース URL と API キーを、整文の互換接続の内容で上書きします。よろしいですか？", "whisparrot", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        ApplyCopyFromCleanup();
    }

    private void ApplyCopyFromCleanup()
    {
        _error.Text = "";
        var r = CloudConnectionForm.CopyFromCleanup(_settings.Current.Cleanup.Compatible, _protector);
        if (!r.Ok) { _error.Text = r.Error!; return; }
        _cUrl.Text = r.BaseUrl;
        _cKey.Password = "";
        _cClearKey.IsChecked = false;
        _cCopied = true;
        _cCopiedKey = r.KeyCipher;
        UpdateCloudTarget();
        UpdateCloudStatus();
    }

    /// <summary>三つの接続項目を既定（未設定）へ戻す。保存して初めて確定する。エンジンの選択は変えない。</summary>
    private void ClearCloudConnection()
    {
        _error.Text = "";
        _cUrl.Text = ""; _cModel.Text = ""; _cLang.Text = CloudRecognitionSettings.DefaultLanguage; _cKey.Password = "";
        _cCopied = false; _cCopiedKey = null;
        _cClearKey.IsChecked = _settings.Current.Recognition.Cloud.ApiKeyDpapi is not null;
        UpdateCloudTarget();
        UpdateCloudStatus();
    }

    private UIElement BuildCleanup()
    {
        _apiKey.ToolTip = "保存済みの場合は空のままにすると維持されます";
        _baseUrl.TextChanged += (_, _) => UpdateDest();
        _cleanChatGpt.Checked += (_, _) => UpdateChatGptVisibility();
        _cleanChatGpt.Unchecked += (_, _) => UpdateChatGptVisibility();
        _cgLogin.Click += (_, _) => OnChatGptLogin();
        _cgLogout.Click += (_, _) => OnChatGptLogout();
        var cgButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        cgButtons.Children.Add(_cgLogin); cgButtons.Children.Add(_cgLogout);
        var pasteButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var pasteOk = new Button { Content = "貼り付けた URL で続行", Padding = new Thickness(10, 1, 10, 1), Margin = new Thickness(0, 0, 8, 0) };
        var pasteCancel = new Button { Content = "ログインを取り消す", Padding = new Thickness(10, 1, 10, 1) };
        pasteOk.Click += (_, _) => OnChatGptPaste();
        pasteCancel.Click += (_, _) => _login?.Cancel();
        pasteButtons.Children.Add(pasteOk); pasteButtons.Children.Add(pasteCancel);
        _cgPaste.Children.Add(new TextBlock { Text = "ブラウザでサインインしてください。自動で完了しない場合は、サインイン後にブラウザのアドレス欄に表示された URL 全体を貼り付けてください（別の端末のブラウザでも可）。", TextWrapping = TextWrapping.Wrap });
        _cgPaste.Children.Add(Row("サインイン URL", _cgAuthUrl));
        _cgPaste.Children.Add(Row("リダイレクト URL", _cgPasted));
        _cgPaste.Children.Add(pasteButtons);
        _cgPanel.Children.Add(_cgStatus);
        _cgPanel.Children.Add(cgButtons);
        _cgPanel.Children.Add(_cgPaste);
        _cgPanel.Children.Add(Row("モデル名", _cgModel));
        _cgPanel.Children.Add(new TextBlock { Text = "ログイン・ログアウトは保存を待たず、すぐに反映されます。ログイン情報（token）は config.json とは別のファイルに、Windows のユーザー保護（DPAPI）で暗号化して保存します。ブラウザでのサインイン自体の通信は、このアプリのプロキシ設定の対象外です。", TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.DimGray, Margin = new Thickness(0, 4, 0, 0) });
        return Group("整文", _cleanOff, _cleanCompat,
            Row("ベース URL", _baseUrl), _destHost, Row("API キー", _apiKey), _clearKey, Row("モデル名", _cleanModel),
            _cleanChatGpt, _cgPanel,
            new TextBlock { Text = "「互換エンドポイント」を選ぶと、認識したテキストだけをここで指定した送信先へ送って整文します（失敗・未設定・時間切れのときは認識結果をそのまま入力）。API キーは Windows のユーザー保護（DPAPI）で暗号化して保存します。「ChatGPT」は公式の Sign in with ChatGPT でログインし、同様に認識したテキストだけを送ります。", TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.DimGray });
    }

    private void UpdateChatGptVisibility()
    {
        _cgPanel.Visibility = _cleanChatGpt.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RefreshChatGptStatus();
    }

    /// <summary>接続状態の表示。未ログインで選ばれていても、ブラウザは開かず操作を求めるだけ。</summary>
    private void RefreshChatGptStatus()
    {
        var st = _chatGpt.GetStatus();
        var busy = _login is not null;
        _cgStatus.Text = st.State switch
        {
            ChatGptConnection.LoggedIn => "接続状態: ログイン済み",
            ChatGptConnection.LoggedOut => "接続状態: 未ログイン — 「ログイン」を押してブラウザでサインインしてください（整文は行われず、認識結果がそのまま入力されます）",
            _ => $"接続状態: ログインが無効です（{st.Detail}）— 「ログイン」でやり直してください",
        };
        _cgLogin.IsEnabled = !busy;
        _cgLogin.Content = st.State == ChatGptConnection.LoggedIn ? "再ログイン" : "ログイン";
        _cgLogout.IsEnabled = !busy && st.State != ChatGptConnection.LoggedOut;
    }

    private async void OnChatGptLogin()
    {
        _error.Text = "";
        if (_login is not null) return;
        ChatGptAuthService.ChatGptLoginAttempt attempt;
        try { attempt = _chatGpt.BeginLogin(); }
        catch (ChatGptLoginException ex) { _error.Text = ex.Message; return; }
        _login = attempt;
        _cgAuthUrl.Text = attempt.AuthorizeUrl.AbsoluteUri;
        _cgPasted.Text = "";
        _cgPaste.Visibility = Visibility.Visible;
        RefreshChatGptStatus();
        try
        {
            // 利用者が「ログイン」を押した結果としてだけ開く。
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(attempt.AuthorizeUrl.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception) { _error.Text = "ブラウザを開けませんでした。下の URL を手動で開いてください。"; }
        try
        {
            await attempt.Completion;
            _error.Text = "";
        }
        catch (ChatGptLoginException ex) when (ex.Kind != ChatGptLoginFailure.Cancelled) { _error.Text = ex.Message; }
        catch (ChatGptLoginException) { _error.Text = "ログインを取り消しました。"; }
        finally
        {
            attempt.Dispose();
            _login = null;
            _cgPaste.Visibility = Visibility.Collapsed;
            RefreshChatGptStatus();
        }
    }

    private void OnChatGptPaste()
    {
        try { _login?.SubmitPasted(_cgPasted.Text); _error.Text = ""; }
        catch (ChatGptLoginException ex) { _error.Text = ex.Message; }
    }

    private void OnChatGptLogout()
    {
        _error.Text = "";
        try { _chatGpt.Logout(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _error.Text = "ログイン情報のファイルを削除できませんでした。"; }
        RefreshChatGptStatus();
    }

    private UIElement AuthChoice()
    {
        var p = new StackPanel();
        p.Children.Add(_pxAuthCred); p.Children.Add(_pxAuthWindows);
        return p;
    }

    /// <summary>認証方式は排他。Windows のログオン情報を選んだらユーザー名・パスワード欄は使えない。</summary>
    private void UpdateProxyAuthEnabled()
    {
        var creds = _pxAuthWindows.IsChecked != true;
        _pxUser.IsEnabled = creds;
        _pxPass.IsEnabled = creds;
        _clearPxPass.IsEnabled = creds && _settings.Current.Proxy.ManualPasswordDpapi is not null;
    }

    private UIElement BuildNetwork()
    {
        var env = _network.EnvironmentProxyDisplay();
        var lines = new List<string> { env is null ? "環境変数のプロキシ: なし（直結）" : $"環境変数のプロキシ: {env}" };
        lines.Add("URL にユーザー名がなければ、Windows のログオン情報でプロキシの認証（NTLM / Negotiate）に応答します。");
        lines.AddRange(_network.Environment.Conflicts);
        _pxAuthCred.Checked += (_, _) => UpdateProxyAuthEnabled();
        _pxAuthWindows.Checked += (_, _) => UpdateProxyAuthEnabled();
        return Group("通信", _pxEnv, new TextBlock { Text = string.Join("\n", lines), Foreground = System.Windows.Media.Brushes.DimGray, Margin = new Thickness(20, 0, 0, 4) },
            _pxManual, Row("URL", _pxUrl), Row("認証", AuthChoice()), Row("ユーザー名", _pxUser), Row("パスワード", _pxPass), _clearPxPass, _pxNone,
            new TextBlock { Text = "手動指定は http://host:port または https://host:port の形式（https:// はプロキシへの接続を TLS で行い、証明書は OS の信頼ストアで検証します）。", Foreground = System.Windows.Media.Brushes.DimGray });
    }

    private UIElement BuildStartup() => Group("起動・更新", _autostartBox,
        new TextBlock { Text = $"whisparrot {AppInfo.DisplayVersion} / {RuntimeInformation.ProcessArchitecture}" },
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
        _cUrl.Text = s.Recognition.Cloud.BaseUrl;
        _cModel.Text = s.Recognition.Cloud.Model;
        _cLang.Text = s.Recognition.Cloud.Language;
        _engLocal.IsChecked = s.Recognition.Engine == RecognitionEngine.Local;
        _engCloud.IsChecked = s.Recognition.Engine == RecognitionEngine.Cloud;
        UpdateEngineVisibility();

        _cleanOff.IsChecked = s.Cleanup.Provider == CleanupProvider.Off;
        _cleanCompat.IsChecked = s.Cleanup.Provider == CleanupProvider.Compatible;
        _cleanChatGpt.IsChecked = s.Cleanup.Provider == CleanupProvider.ChatGpt;
        _baseUrl.Text = s.Cleanup.Compatible.BaseUrl;
        _cleanModel.Text = s.Cleanup.Compatible.Model;
        _cgModel.Text = s.Cleanup.ChatGpt.Model;
        UpdateChatGptVisibility();
        _clearKey.IsEnabled = s.Cleanup.Compatible.ApiKeyDpapi is not null;
        UpdateDest();

        _pxEnv.IsChecked = s.Proxy.Mode == ProxyMode.Environment;
        _pxManual.IsChecked = s.Proxy.Mode == ProxyMode.Manual;
        _pxNone.IsChecked = s.Proxy.Mode == ProxyMode.None;
        _pxUrl.Text = s.Proxy.ManualUrl;
        _pxUser.Text = s.Proxy.ManualUsername;
        _pxAuthCred.IsChecked = s.Proxy.ManualAuth == ProxyManualAuth.Credentials;
        _pxAuthWindows.IsChecked = s.Proxy.ManualAuth == ProxyManualAuth.Windows;
        UpdateProxyAuthEnabled();

        _autostartBox.IsChecked = s.Autostart;
        // ローカルの実行方式（最後のローカル結果）と、直近のセッションの認識先は別に表示する（クラウドの使用でローカルの欄は変わらない）。
        _backend.Text = "ローカルの実際の実行方式: " + (_backendText() ?? "未実行");
        _lastRecognition.Text = "今回の認識: " + (_lastRecognitionText?.Invoke() ?? "未実行");
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
            Recognition = cur.Recognition with
            {
                Model = _modelRadios.First(kv => kv.Value.IsChecked == true).Key,
                Engine = _engCloud.IsChecked == true ? RecognitionEngine.Cloud : RecognitionEngine.Local,
                Cloud = ReadCloudForm(),
            },
            Cleanup = cur.Cleanup with
            {
                Provider = _cleanCompat.IsChecked == true ? CleanupProvider.Compatible : _cleanChatGpt.IsChecked == true ? CleanupProvider.ChatGpt : CleanupProvider.Off,
                Compatible = new CompatibleCleanupSettings(_baseUrl.Text.Trim(), apiKey, _cleanModel.Text.Trim()),
                ChatGpt = new ChatGptCleanupSettings(_cgModel.Text.Trim()),
            },
            Proxy = new ProxySettings(_pxManual.IsChecked == true ? ProxyMode.Manual : _pxNone.IsChecked == true ? ProxyMode.None : ProxyMode.Environment,
                _pxUrl.Text.Trim(), _pxUser.Text.Trim(), pxPass,
                _pxAuthWindows.IsChecked == true ? ProxyManualAuth.Windows : ProxyManualAuth.Credentials),
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
