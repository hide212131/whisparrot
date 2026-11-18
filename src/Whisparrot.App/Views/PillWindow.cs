using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Whisparrot.App.Windows;
using Whisparrot.Core;
using Whisparrot.Core.Settings;
using static Whisparrot.App.Windows.NativeMethods;

namespace Whisparrot.App.Views;

/// <summary>
/// 最小ピル（設計 §6.1）。280x52 DIP・角丸 26。WS_EX_NOACTIVATE + MA_NOACTIVATE + SW_SHOWNOACTIVATE 相当で
/// フォーカスを奪わない。録音中は 5 本のレベルと 停止 / 取消、認識中・入力中は文言と 取消。
/// </summary>
internal sealed class PillWindow : Window
{
    private readonly Rectangle[] _bars = new Rectangle[5];
    private readonly TextBlock _label = new();
    private readonly StackPanel _levels = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _stop = MakeButton("■", "停止");
    private readonly Button _cancel = MakeButton("×", "取消");
    private readonly float[] _history = new float[5];

    public event Action? StopClicked;
    public event Action? CancelClicked;

    public PillWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Width = 280;
        Height = 52;
        Focusable = false;

        for (var i = 0; i < _bars.Length; i++)
        {
            _bars[i] = new Rectangle { Width = 5, Height = 6, Margin = new Thickness(2, 0, 2, 0), RadiusX = 2, RadiusY = 2, Fill = Brushes.White, VerticalAlignment = VerticalAlignment.Center };
            _levels.Children.Add(_bars[i]);
        }
        _label.Foreground = Brushes.White;
        _label.VerticalAlignment = VerticalAlignment.Center;
        _label.HorizontalAlignment = HorizontalAlignment.Center;

        var grid = new Grid { Margin = new Thickness(18, 0, 14, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var mic = new Ellipse { Width = 14, Height = 14, Fill = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(mic, "マイク");
        Grid.SetColumn(mic, 0);
        var center = new Grid { Margin = new Thickness(12, 0, 12, 0) };
        center.Children.Add(_levels);
        center.Children.Add(_label);
        Grid.SetColumn(center, 1);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(_stop);
        buttons.Children.Add(_cancel);
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(mic);
        grid.Children.Add(center);
        grid.Children.Add(buttons);

        Content = new Border { CornerRadius = new CornerRadius(26), Background = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B)), Child = grid };
        _stop.Click += (_, _) => StopClicked?.Invoke();
        _cancel.Click += (_, _) => CancelClicked?.Invoke();

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
            HwndSource.FromHwnd(hwnd)?.AddHook(NoActivateHook);
        };
        ShowState(SessionState.Idle);
    }

    private static IntPtr NoActivateHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(MA_NOACTIVATE);
        }
        return IntPtr.Zero;
    }

    private static Button MakeButton(string glyph, string name)
    {
        var b = new Button
        {
            Content = glyph,
            Width = 28,
            Height = 28,
            Margin = new Thickness(2, 0, 0, 0),
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
            BorderThickness = new Thickness(0),
            Focusable = false,
            ToolTip = name,
        };
        AutomationProperties.SetName(b, name);
        return b;
    }

    /// <summary>UI スレッドから呼ぶ。Idle で隠し、それ以外は表示する（フォーカスは奪わない）。</summary>
    public void ShowState(SessionState state, RecognitionEngine? engine = null)
    {
        switch (state)
        {
            case SessionState.Recording:
                _levels.Visibility = Visibility.Visible;
                _label.Visibility = Visibility.Collapsed;
                _stop.IsEnabled = true;
                _cancel.IsEnabled = true;
                _stop.Visibility = Visibility.Visible;
                Array.Clear(_history);
                Redraw();
                Present();
                break;
            case SessionState.Recognizing:
                SetLabel(engine == RecognitionEngine.Cloud ? "クラウド認識中" : "認識中", cancelEnabled: true);
                Present();
                break;
            case SessionState.Polishing:
                SetLabel("整文中", cancelEnabled: true);
                Present();
                break;
            case SessionState.Inserting:
                SetLabel("入力中", cancelEnabled: false);
                Present();
                break;
            case SessionState.Failed:
                SetLabel("入力できませんでした", cancelEnabled: true);
                Present();
                break;
            default:
                Hide();
                break;
        }
    }

    private void SetLabel(string text, bool cancelEnabled)
    {
        _levels.Visibility = Visibility.Collapsed;
        _label.Visibility = Visibility.Visible;
        _label.Text = text;
        AutomationProperties.SetName(_label, text);
        _stop.Visibility = Visibility.Collapsed;
        _cancel.IsEnabled = cancelEnabled;
    }

    public void PushLevel(float level)
    {
        for (var i = 0; i < _history.Length - 1; i++) _history[i] = _history[i + 1];
        _history[^1] = level;
        Redraw();
    }

    private void Redraw()
    {
        for (var i = 0; i < _bars.Length; i++) _bars[i].Height = 6 + _history[i] * 24;
    }

    private void Present()
    {
        if (IsVisible) return; // 位置は表示開始時にだけ決める（録音中にフォーカスが移っても飛ばない）
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        // 録音開始時の前景ウィンドウのあるモニターの作業領域の下中央、24 DIP 上。
        var target = GetForegroundWindow();
        var monitor = MonitorFromWindow(target == IntPtr.Zero ? hwnd : target, 2 /* MONITOR_DEFAULTTOPRIMARY */);
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(monitor, ref info)) return;
        var dpi = GetDpiForWindow(hwnd);
        var scale = (dpi == 0 ? 96 : dpi) / 96.0;
        int w = (int)Math.Round(Width * scale), h = (int)Math.Round(Height * scale);
        var work = info.rcWork;
        var x = work.Left + ((work.Right - work.Left) - w) / 2;
        var y = work.Bottom - h - (int)Math.Round(24 * scale);
        Show(); // ShowActivated=false
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }
}
