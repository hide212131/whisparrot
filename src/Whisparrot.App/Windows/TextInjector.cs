using System.Runtime.InteropServices;
using Whisparrot.Core;
using static Whisparrot.App.Windows.NativeMethods;

namespace Whisparrot.App.Windows;

/// <summary>
/// クリップボード方式の投入（設計 §10.1）。元の内容を退避 → テキスト設定 → Ctrl+V → 復元。
/// Enter は送らない。クリップボードは STA の専用スレッドで扱う。
/// M1 の範囲: 退避は HGLOBAL 形式（テキスト / HTML / RTF / HDROP 等）。GDI ハンドル形式
/// （CF_ENHMETAFILE 等）で安全に複製できないものがあれば、元の内容を触らず投入を中止する。
/// </summary>
internal sealed class TextInjector : ITextInjector
{
    internal static readonly TimeSpan ModifierWait = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan ClipboardOpenWait = TimeSpan.FromSeconds(1);
    /// <summary>貼り付け先が読み取る猶予。設計 §10.1 の初期値 300 ms（実機で 2 秒まで調整）。</summary>
    internal static readonly TimeSpan PasteGrace = TimeSpan.FromMilliseconds(300);

    private const uint CF_BITMAP = 2, CF_METAFILEPICT = 3, CF_PALETTE = 9, CF_DIB = 8, CF_DIBV5 = 17, CF_ENHMETAFILE = 14;
    private const uint CF_TEXT = 1, CF_OEMTEXT = 7, CF_LOCALE = 16;

    public Task<InjectResult> InjectAsync(string text, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<InjectResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(InjectOnSta(text)); }
            catch (Exception ex) { tcs.SetResult(new InjectResult(InjectOutcome.Failed, $"入力に失敗しました: {ex.Message}")); }
        }) { IsBackground = true, Name = "whisparrot-inject" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static InjectResult InjectOnSta(string text)
    {
        if (!ForegroundIsExternal()) return Fail("入力先のウィンドウが見つかりません");
        if (!WaitModifiersReleased()) return Fail("修飾キーが押されたままです");

        if (!OpenClipboardRetry()) return Fail("クリップボードを開けませんでした");
        List<(uint Format, byte[] Data)> snapshot;
        try
        {
            if (!TrySnapshot(out snapshot, out var why))
            {
                CloseClipboard();
                return Fail(why);
            }
            EmptyClipboard();
            if (!SetText(text))
            {
                // 退避済みの内容へ戻して中止する。
                Restore(snapshot);
                CloseClipboard();
                return Fail("クリップボードへ設定できませんでした");
            }
        }
        catch
        {
            CloseClipboard();
            throw;
        }
        CloseClipboard();
        var ourSequence = GetClipboardSequenceNumber();

        var sent = SendCtrlV();
        Thread.Sleep(PasteGrace);

        // 復元: 自分が設定してから変わっていなければ戻す。別のコピーがあればそちらを優先する。
        if (!OpenClipboardRetry()) return Fail("貼り付けましたが、クリップボードを復元できませんでした");
        try
        {
            if (GetClipboardSequenceNumber() == ourSequence)
            {
                EmptyClipboard();
                Restore(snapshot);
            }
        }
        finally
        {
            CloseClipboard();
        }
        return sent ? new InjectResult(InjectOutcome.Success) : Fail("キー入力を送れませんでした");
    }

    /// <summary>
    /// 診断用: 複数形式（Unicode テキスト / HTML / HDROP）を載せたクリップボードに対し、
    /// 「退避 → 投入テキストを設定 → 復元」を行い、全形式が元どおり戻るか、および別アプリがコピーした場合は復元しないことを確認する。
    /// 前景ウィンドウ確認と Ctrl+V の送信は行わない。
    /// </summary>
    internal static string ClipboardRoundTripProbe()
    {
        string result = "no result";
        var thread = new Thread(() => result = RoundTrip()) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;

        static string RoundTrip()
        {
            var html = RegisterClipboardFormatW("HTML Format");
            var original = new List<(uint Format, byte[] Data)>
            {
                (CF_UNICODETEXT, System.Text.Encoding.Unicode.GetBytes("元のテキスト\0")),
                (html, System.Text.Encoding.UTF8.GetBytes("Version:0.9\r\n<html><body>元</body></html>\0")),
                (15 /* CF_HDROP */, new byte[] { 20, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0 }),
            };
            if (!OpenClipboardRetry()) return "FAIL: cannot open clipboard (non-interactive window station?)";
            EmptyClipboard();
            foreach (var (f, d) in original) SetGlobal(f, d);
            CloseClipboard();

            // 1) 通常: 退避 → 設定 → （sequence 不変）→ 復元
            OpenClipboardRetry();
            TrySnapshot(out var snap, out _);
            EmptyClipboard();
            SetText("投入テキスト");
            CloseClipboard();
            var seq = GetClipboardSequenceNumber();
            OpenClipboardRetry();
            var unchanged = GetClipboardSequenceNumber() == seq;
            if (unchanged) { EmptyClipboard(); Restore(snap); }
            CloseClipboard();
            var restored = ReadAll();
            var same = original.All(o => restored.Any(r => r.Format == o.Format && r.Data.AsSpan().SequenceEqual(o.Data)));

            // 2) 別アプリがコピーした場合: sequence が変わるので復元しない
            OpenClipboardRetry();
            TrySnapshot(out var snap2, out _);
            EmptyClipboard();
            SetText("投入テキスト");
            CloseClipboard();
            var seq2 = GetClipboardSequenceNumber();
            OpenClipboardRetry(); EmptyClipboard(); SetGlobal(CF_UNICODETEXT, System.Text.Encoding.Unicode.GetBytes("他アプリのコピー\0")); CloseClipboard();
            OpenClipboardRetry();
            var changed = GetClipboardSequenceNumber() != seq2;
            if (!changed) { EmptyClipboard(); Restore(snap2); }
            CloseClipboard();
            var after = ReadAll();
            var kept = after.Any(r => r.Format == CF_UNICODETEXT && System.Text.Encoding.Unicode.GetString(r.Data).StartsWith("他アプリのコピー"));

            return $"snapshot_formats={snap.Count} all_original_formats_restored={same} other_app_copy_preserved={kept}";
        }

        static List<(uint Format, byte[] Data)> ReadAll()
        {
            var list = new List<(uint, byte[])>();
            OpenClipboardRetry();
            TrySnapshot(out list, out _);
            CloseClipboard();
            return list;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormatW(string name);

    private static InjectResult Fail(string message) => new(InjectOutcome.Failed, message);

    private static bool ForegroundIsExternal()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || hwnd == GetShellWindow() || hwnd == GetDesktopWindow()) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid != Environment.ProcessId;
    }

    private static bool WaitModifiersReleased()
    {
        var deadline = DateTime.UtcNow + ModifierWait;
        int[] keys = [VK_LWIN, VK_RWIN, VK_CONTROL, VK_MENU, VK_SHIFT];
        while (true)
        {
            if (!keys.Any(k => (GetAsyncKeyState(k) & 0x8000) != 0)) return true;
            if (DateTime.UtcNow >= deadline) return false;
            Thread.Sleep(10);
        }
    }

    private static bool OpenClipboardRetry()
    {
        var deadline = DateTime.UtcNow + ClipboardOpenWait;
        while (true)
        {
            if (OpenClipboard(IntPtr.Zero)) return true;
            if (DateTime.UtcNow >= deadline) return false;
            Thread.Sleep(15);
        }
    }

    private static bool TrySnapshot(out List<(uint Format, byte[] Data)> snapshot, out string reason)
    {
        snapshot = [];
        reason = "";
        var formats = new List<uint>();
        for (var f = EnumClipboardFormats(0); f != 0; f = EnumClipboardFormats(f)) formats.Add(f);
        var hasDib = formats.Contains(CF_DIB) || formats.Contains(CF_DIBV5);
        var hasUnicode = formats.Contains(CF_UNICODETEXT);

        foreach (var f in formats)
        {
            // OS が他形式から自動合成する形式は退避しない（復元時に OS が再合成する）。
            if ((f == CF_BITMAP && hasDib) || ((f == CF_TEXT || f == CF_OEMTEXT || f == CF_LOCALE) && hasUnicode)) continue;
            if (f == CF_BITMAP || f == CF_METAFILEPICT || f == CF_PALETTE || f == CF_ENHMETAFILE || (f >= 0x80 && f <= 0x8F))
            {
                reason = "クリップボードの内容を安全に退避できないため、入力を中止しました";
                return false;
            }
            var h = GetClipboardData(f);
            if (h == IntPtr.Zero)
            {
                reason = "クリップボードの内容を取得できないため、入力を中止しました";
                return false;
            }
            var size = (long)GlobalSize(h);
            var p = GlobalLock(h);
            if (size <= 0 || p == IntPtr.Zero)
            {
                reason = "クリップボードの内容を複製できないため、入力を中止しました";
                return false;
            }
            var bytes = new byte[size];
            Marshal.Copy(p, bytes, 0, (int)size);
            GlobalUnlock(h);
            snapshot.Add((f, bytes));
        }
        return true;
    }

    private static bool SetText(string text)
    {
        var bytes = System.Text.Encoding.Unicode.GetBytes(text + "\0");
        return SetGlobal(CF_UNICODETEXT, bytes);
    }

    private static void Restore(List<(uint Format, byte[] Data)> snapshot)
    {
        foreach (var (format, data) in snapshot) SetGlobal(format, data);
    }

    private static bool SetGlobal(uint format, byte[] data)
    {
        var h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
        if (h == IntPtr.Zero) return false;
        var p = GlobalLock(h);
        if (p == IntPtr.Zero) { GlobalFree(h); return false; }
        Marshal.Copy(data, 0, p, data.Length);
        GlobalUnlock(h);
        if (SetClipboardData(format, h) == IntPtr.Zero)
        {
            GlobalFree(h);
            return false;
        }
        return true; // 所有権はクリップボードへ移った
    }

    private static bool SendCtrlV()
    {
        static INPUT Key(ushort vk, bool up) => new()
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 } },
        };
        INPUT[] inputs = [Key(VK_CONTROL, false), Key(VK_V, false), Key(VK_V, true), Key(VK_CONTROL, true)];
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;
    }
}
