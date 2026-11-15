using System.Windows.Interop;
using Whisparrot.Core;

namespace Whisparrot.App.Windows;

/// <summary>
/// メッセージ専用ウィンドウ + <c>RegisterHotKey</c>（設計 §5）。候補を順に試し、登録に成功した最初のものを採用する。
/// 全候補が失敗しても例外にしない（トレイから開始/停止できる）。Win キーを奪う低レベルフックは使わない。
/// </summary>
internal sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0x7701;
    private HwndSource? _source;
    private IntPtr _hwnd;

    public event Action? Pressed;

    /// <summary>採用したキー。全滅なら null。</summary>
    public Hotkey? Adopted { get; private set; }

    /// <summary>候補ごとの結果（キー表示, 成功か, Win32 エラー）。採用キーとともに記録・表示に使う。</summary>
    public List<(string Display, bool Ok, int Error)> Attempts { get; } = [];

    public Hotkey? Register(IEnumerable<Hotkey> candidates)
    {
        _source = new HwndSource(new HwndSourceParameters("whisparrot-hotkey") { ParentWindow = NativeMethods.HWND_MESSAGE, Width = 0, Height = 0 });
        _source.AddHook(WndProc);
        _hwnd = _source.Handle;
        Adopted = HotkeyNegotiator.Negotiate(candidates, c =>
        {
            var ok = NativeMethods.RegisterHotKey(_hwnd, HotkeyId, (uint)c.Modifiers | NativeMethods.MOD_NOREPEAT, c.VirtualKey);
            Attempts.Add((c.Display, ok, ok ? 0 : System.Runtime.InteropServices.Marshal.GetLastWin32Error()));
            return ok;
        });
        return Adopted;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (Adopted is not null) NativeMethods.UnregisterHotKey(_hwnd, HotkeyId);
        Adopted = null;
        _source?.Dispose();
        _source = null;
    }
}
