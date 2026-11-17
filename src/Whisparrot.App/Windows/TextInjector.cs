using System.Runtime.InteropServices;
using Whisparrot.Core;
using static Whisparrot.App.Windows.NativeMethods;

namespace Whisparrot.App.Windows;

/// <summary>
/// クリップボード方式の投入（設計 §10.1）。元の内容を退避 → テキスト設定 → Ctrl+V → 復元。
/// Enter は送らない。クリップボードは STA の専用スレッドで扱う。
/// 退避は HGLOBAL 形式（テキスト / HTML / RTF / HDROP 等）を形式ごとに独立して行い、取得できない形式・
/// バイト複製できない GDI ハンドル形式（CF_ENHMETAFILE 等）はその形式だけを飛ばす（飛ばした形式は復元されない）。
/// 投入を中止するのは、クリップボードを開けないなど本当に進めない場合に限る。
/// </summary>
internal sealed class TextInjector : ITextInjector
{
    internal static readonly TimeSpan ModifierWait = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan ClipboardOpenWait = TimeSpan.FromSeconds(1);
    /// <summary>
    /// 貼り付け先が読み取る猶予。設計 §10.1 は初期値 300 ms・実機で 2 秒まで調整可。
    /// 新しい Windows のメモ帳では貼り付け時のクリップボード読み取りが非同期で、300 ms では間に合わず
    /// 復元後の内容を読まれる（または何も貼られない）事例があったため 1500 ms とした。
    /// </summary>
    internal static readonly TimeSpan PasteGrace = TimeSpan.FromMilliseconds(1500);

    private const uint CF_BITMAP = 2, CF_METAFILEPICT = 3, CF_PALETTE = 9, CF_DIB = 8, CF_DIBV5 = 17, CF_ENHMETAFILE = 14;
    private const uint CF_TEXT = 1, CF_OEMTEXT = 7, CF_LOCALE = 16;

    /// <summary>GetClipboardData が NULL のとき、1 回だけ再試行するまでの間隔（遅延描画の所有者の描画待ち）。</summary>
    internal static readonly TimeSpan RenderRetryDelay = TimeSpan.FromMilliseconds(30);

    internal enum SkipReason { NotRetrievable, NotDuplicable, GdiHandle }

    /// <summary>退避できずに飛ばした形式。診断には形式 ID・（標準形式なら）名前・理由だけを出し、内容は出さない。</summary>
    internal readonly record struct SkippedFormat(uint Format, SkipReason Reason)
    {
        public override string ToString()
        {
            var name = StandardName(Format);
            var reason = Reason switch { SkipReason.NotRetrievable => "not_retrievable", SkipReason.NotDuplicable => "not_duplicable", _ => "gdi_handle" };
            return name == null ? $"{Format}({reason})" : $"{Format}:{name}({reason})";
        }
    }

    internal static string FormatSkipped(IReadOnlyCollection<SkippedFormat> skipped) =>
        skipped.Count == 0 ? "none" : string.Join(",", skipped);

    private static string? StandardName(uint f) => f switch
    {
        1 => "CF_TEXT", 2 => "CF_BITMAP", 3 => "CF_METAFILEPICT", 4 => "CF_SYLK", 5 => "CF_DIF", 6 => "CF_TIFF",
        7 => "CF_OEMTEXT", 8 => "CF_DIB", 9 => "CF_PALETTE", 10 => "CF_PENDATA", 11 => "CF_RIFF", 12 => "CF_WAVE",
        13 => "CF_UNICODETEXT", 14 => "CF_ENHMETAFILE", 15 => "CF_HDROP", 16 => "CF_LOCALE", 17 => "CF_DIBV5",
        0x80 => "CF_OWNERDISPLAY", 0x81 => "CF_DSPTEXT", 0x82 => "CF_DSPBITMAP", 0x83 => "CF_DSPMETAFILEPICT", 0x8E => "CF_DSPENHMETAFILE",
        _ => null,
    };

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

        return InjectClipboard(text, SendCtrlV);
    }

    /// <summary>
    /// 前景確認・修飾キー待ちの後の本体: 退避 → テキスト設定 → 貼り付け送信 → 復元。
    /// 貼り付けの送信を差し替えられるようにしてあり、テストはキー入力なしでクリップボード側だけを検証する。
    /// </summary>
    internal static InjectResult InjectClipboard(string text, Func<bool> sendPaste, Action<IReadOnlyList<SkippedFormat>>? onSkipped = null,
        TimeSpan? grace = null, Action<string>? trace = null)
    {
        if (!OpenClipboardRetry()) return Fail("クリップボードを開けませんでした");
        List<(uint Format, byte[] Data)> snapshot;
        try
        {
            snapshot = Snapshot(out var skipped);
            if (skipped.Count > 0) onSkipped?.Invoke(skipped);
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
        trace?.Invoke($"sequence_after_set={ourSequence}");

        var sent = sendPaste();
        trace?.Invoke($"paste_sent={sent}");
        Thread.Sleep(grace ?? PasteGrace);

        // 復元: 自分が設定してから変わっていなければ戻す。別のコピーがあればそちらを優先する。
        if (!OpenClipboardRetry()) return Fail("貼り付けましたが、クリップボードを復元できませんでした");
        try
        {
            var seqBeforeRestore = GetClipboardSequenceNumber();
            var unchanged = seqBeforeRestore == ourSequence;
            trace?.Invoke($"sequence_before_restore={seqBeforeRestore} unchanged={unchanged}");
            if (unchanged)
            {
                EmptyClipboard();
                Restore(snapshot);
            }
            trace?.Invoke($"restored={unchanged}");
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
            var snap = Snapshot(out var skipped);
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
            var snap2 = Snapshot(out _);
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

            return $"snapshot_formats={snap.Count} all_original_formats_restored={same} other_app_copy_preserved={kept} skipped_formats={FormatSkipped(skipped)}";
        }

        static List<(uint Format, byte[] Data)> ReadAll()
        {
            OpenClipboardRetry();
            var list = Snapshot(out _);
            CloseClipboard();
            return list;
        }
    }

    /// <summary>
    /// 診断用: 今のクリップボードを退避するとしたら、何形式が退避でき、どの形式が飛ばされるかを出す。
    /// クリップボードは変更しない。出力は形式 ID・名前・理由と件数だけで、内容は出さない。
    /// </summary>
    internal static string ClipboardInspectProbe()
    {
        string result = "no result";
        var thread = new Thread(() =>
        {
            if (!OpenClipboardRetry()) { result = "FAIL: cannot open clipboard (non-interactive window station?)"; return; }
            try
            {
                var snap = Snapshot(out var skipped);
                result = $"snapshot_formats={snap.Count} skipped_formats={FormatSkipped(skipped)}";
            }
            finally { CloseClipboard(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    internal const string ProbeText = "whisparrot-inject-probe-12345";

    /// <summary>
    /// 診断用: 5 秒のカウントダウンの間に対象アプリへフォーカスを移してもらい、本番と同じ InjectClipboard 経路で
    /// 固定文字列を投入して各ステップを出力する。出すのは前景のプロセス名とクラス名（タイトルは出さない）、
    /// sequence 番号、成否、長さだけで、クリップボードの内容は出さない。
    /// </summary>
    internal static int InjectProbe(int? graceMs, Action<string> write)
    {
        var grace = graceMs is { } g ? TimeSpan.FromMilliseconds(g) : PasteGrace;
        write($"grace_ms={(int)grace.TotalMilliseconds}");
        for (var i = 5; i >= 1; i--)
        {
            write($"countdown={i} (focus the target app now)");
            Thread.Sleep(1000);
        }
        var exit = 1;
        var thread = new Thread(() =>
        {
            write($"foreground={DescribeForeground()}");
            var external = ForegroundIsExternal();
            write($"foreground_external={external}");
            if (!external) { write("result=Failed 入力先のウィンドウが見つかりません"); return; }
            if (!WaitModifiersReleased()) { write("result=Failed 修飾キーが押されたままです"); return; }
            write($"inject_text_length={ProbeText.Length}");
            var r = InjectClipboard(ProbeText, SendCtrlV, s => write($"skipped_formats={FormatSkipped(s)}"), grace, write);
            write($"result={r.Outcome}{(r.Message is { } m ? " " + m : "")}");
            if (r.Outcome == InjectOutcome.Success) exit = 0;
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return exit;
    }

    private static string DescribeForeground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return "none";
        GetWindowThreadProcessId(hwnd, out var pid);
        string process;
        try { process = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
        catch { process = "unknown"; }
        var cls = new System.Text.StringBuilder(256);
        GetClassNameW(hwnd, cls, cls.Capacity);
        return $"process={process} class={cls}";
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder name, int maxCount);

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

    /// <summary>
    /// 形式ごとに独立して退避する。取得できない形式・複製できない形式は、その形式だけを飛ばして続行する
    /// （遅延描画の所有者が描画しない・終了している等は実環境で普通に起こる）。飛ばした形式は skipped に入る。
    /// </summary>
    private static List<(uint Format, byte[] Data)> Snapshot(out List<SkippedFormat> skipped)
    {
        var snapshot = new List<(uint Format, byte[] Data)>();
        skipped = [];
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
                skipped.Add(new SkippedFormat(f, SkipReason.GdiHandle));
                continue;
            }
            var h = GetClipboardData(f);
            if (h == IntPtr.Zero)
            {
                Thread.Sleep(RenderRetryDelay);
                h = GetClipboardData(f);
            }
            if (h == IntPtr.Zero)
            {
                skipped.Add(new SkippedFormat(f, SkipReason.NotRetrievable));
                continue;
            }
            var bytes = CopyGlobal(h);
            if (bytes == null)
            {
                skipped.Add(new SkippedFormat(f, SkipReason.NotDuplicable));
                continue;
            }
            snapshot.Add((f, bytes));
        }
        return snapshot;
    }

    private static byte[]? CopyGlobal(IntPtr h)
    {
        var size = (long)GlobalSize(h);
        if (size <= 0 || size > int.MaxValue) return null;
        var p = GlobalLock(h);
        if (p == IntPtr.Zero) return null;
        try
        {
            var bytes = new byte[size];
            Marshal.Copy(p, bytes, 0, (int)size);
            return bytes;
        }
        finally { GlobalUnlock(h); }
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
