using System.Windows.Interop;
using Whisparrot.Core;

namespace Whisparrot.App.Windows;

/// <summary>
/// メッセージ専用ウィンドウ + <c>RegisterHotKey</c>（設計 §5）。候補を順に試し、登録に成功した最初のものを採用する。
/// 全候補が失敗しても例外にしない（トレイから開始/停止できる）。Win キーを奪う低レベルフックは使わない。
/// </summary>
internal sealed class HotkeyService : IDisposable
{
    private const int HotkeyIdA = 0x7701;
    private const int HotkeyIdB = 0x7702;
    private int _activeId = HotkeyIdA;
    private HwndSource? _source;
    private IntPtr _hwnd;

    public event Action? Pressed;

    /// <summary>採用したキー。全滅なら null。</summary>
    public Hotkey? Adopted { get; private set; }

    /// <summary>候補ごとの結果（キー表示, 成功か, Win32 エラー）。採用キーとともに記録・表示に使う。</summary>
    public List<(string Display, bool Ok, int Error)> Attempts { get; } = [];

    private void EnsureWindow()
    {
        if (_source is not null) return;
        _source = new HwndSource(new HwndSourceParameters("whisparrot-hotkey") { ParentWindow = NativeMethods.HWND_MESSAGE, Width = 0, Height = 0 });
        _source.AddHook(WndProc);
        _hwnd = _source.Handle;
    }

    /// <summary>
    /// 設定画面での変更用。新しいキーの登録に成功してから旧登録を解除する（失敗時は旧キーのまま）。
    /// 登録検査（設計 §6.2）を兼ねる。
    /// </summary>
    public bool TryReplace(Hotkey next, out int win32Error)
    {
        win32Error = 0;
        EnsureWindow();
        if (Adopted is not null && Whisparrot.Core.Settings.HotkeyFormat.SameKey(Adopted, next)) return true;
        var newId = _activeId == HotkeyIdA ? HotkeyIdB : HotkeyIdA;
        if (!NativeMethods.RegisterHotKey(_hwnd, newId, (uint)next.Modifiers | NativeMethods.MOD_NOREPEAT, next.VirtualKey))
        {
            win32Error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            return false;
        }
        if (Adopted is not null) NativeMethods.UnregisterHotKey(_hwnd, _activeId);
        _activeId = newId;
        Adopted = next;
        return true;
    }

    public Hotkey? Register(IEnumerable<Hotkey> candidates)
    {
        EnsureWindow();
        Adopted = HotkeyNegotiator.Negotiate(candidates, c =>
        {
            var ok = NativeMethods.RegisterHotKey(_hwnd, _activeId, (uint)c.Modifiers | NativeMethods.MOD_NOREPEAT, c.VirtualKey);
            Attempts.Add((c.Display, ok, ok ? 0 : System.Runtime.InteropServices.Marshal.GetLastWin32Error()));
            return ok;
        });
        return Adopted;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() is HotkeyIdA or HotkeyIdB)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (Adopted is not null) NativeMethods.UnregisterHotKey(_hwnd, _activeId);
        Adopted = null;
        _source?.Dispose();
        _source = null;
    }
}
