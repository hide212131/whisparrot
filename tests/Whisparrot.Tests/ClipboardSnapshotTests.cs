using System.Runtime.InteropServices;
using System.Text;
using Whisparrot.App.Windows;
using Whisparrot.Core;
using static Whisparrot.App.Windows.NativeMethods;

namespace Whisparrot.Tests;

/// <summary>
/// クリップボード退避/復元（TextInjector.InjectClipboard）の実測テスト。実際の OS クリップボードを使うため、
/// クリップボードを触るテスト同士は並列に動かさない。貼り付けのキー入力と前景ウィンドウ確認は含まない（差し替え）。
/// </summary>
[CollectionDefinition("Clipboard", DisableParallelization = true)]
public sealed class ClipboardCollection;

[Collection("Clipboard")]
public sealed class ClipboardSnapshotTests
{
    private const uint CF_BITMAP = 2, CF_ENHMETAFILE = 14, CF_HDROP = 15;
    private static readonly uint CfHtml = Win32.RegisterClipboardFormatW("HTML Format");

    private static byte[] Utf16(string s) => Encoding.Unicode.GetBytes(s + "\0");
    private static readonly byte[] HdropBytes = [20, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0];
    private static byte[] HtmlBytes => Encoding.UTF8.GetBytes("Version:0.9\r\n<html><body>元</body></html>\0");

    private static (uint Format, byte[] Data)[] ThreeFormats() =>
        [(CF_UNICODETEXT, Utf16("元のテキスト")), (CfHtml, HtmlBytes), (CF_HDROP, HdropBytes)];

    private static void AssertRestored(IEnumerable<(uint Format, byte[] Data)> expected, Dictionary<uint, byte[]> actual)
    {
        foreach (var (f, d) in expected)
        {
            Assert.True(actual.TryGetValue(f, out var got), $"format {f} not restored");
            Assert.True(d.AsSpan().SequenceEqual(got), $"format {f} bytes differ");
        }
    }

    [Fact]
    public void MultiFormatClipboard_IsRestoredByteForByte()
    {
        Win32.Set(ThreeFormats());
        string? seenAtPaste = null;
        var r = TextInjector.InjectClipboard("投入テキスト", () => { seenAtPaste = Win32.ReadUnicodeText(); return true; });
        Assert.Equal(InjectOutcome.Success, r.Outcome);
        Assert.Equal("投入テキスト", seenAtPaste);
        AssertRestored(ThreeFormats(), Win32.ReadAll());
    }

    [Fact]
    public void CopyByAnotherApp_DuringPaste_IsNotOverwrittenByRestore()
    {
        Win32.Set(ThreeFormats());
        var r = TextInjector.InjectClipboard("投入テキスト", () => { Win32.Set([(CF_UNICODETEXT, Utf16("他アプリのコピー"))]); return true; });
        Assert.Equal(InjectOutcome.Success, r.Outcome);
        var after = Win32.ReadAll();
        Assert.StartsWith("他アプリのコピー", Encoding.Unicode.GetString(after[CF_UNICODETEXT]));
        Assert.False(after.ContainsKey(CfHtml));
    }

    [Fact]
    public void EmptyClipboard_Succeeds_AndStaysEmpty()
    {
        Win32.Set([]);
        string? seenAtPaste = null;
        var r = TextInjector.InjectClipboard("投入テキスト", () => { seenAtPaste = Win32.ReadUnicodeText(); return true; });
        Assert.Equal(InjectOutcome.Success, r.Outcome);
        Assert.Equal("投入テキスト", seenAtPaste);
        Assert.Empty(Win32.ReadAll());
    }

    /// <summary>
    /// 実環境の失敗の再現: 遅延描画を登録しただけで描画に応じない所有者が居ると、GetClipboardData が NULL を返す。
    /// 修正前はこの 1 形式のせいで投入全体が中止された。
    /// </summary>
    [Fact]
    public void UnrenderedDelayedFormat_DoesNotAbortInjection_AndOthersAreRestored()
    {
        var delayed = Win32.RegisterClipboardFormatW("Whisparrot.Test.Delayed");
        using var owner = new DelayedRenderOwner(ThreeFormats(), delayedFormats: [delayed, 0xC0DE]);
        string? seenAtPaste = null;
        IReadOnlyList<TextInjector.SkippedFormat>? skipped = null;
        var r = TextInjector.InjectClipboard("投入テキスト", () => { seenAtPaste = Win32.ReadUnicodeText(); return true; }, s => skipped = s);

        Assert.True(owner.RenderRequests >= 1, "scenario check: the owner was asked to render (GetClipboardData reached it)");
        Assert.True(r.Outcome == InjectOutcome.Success, r.Message);
        Assert.Equal("投入テキスト", seenAtPaste);
        var after = Win32.ReadAll();
        AssertRestored(ThreeFormats(), after);
        Assert.False(after.ContainsKey(delayed)); // 取得できなかった形式は復元されない
        Assert.NotNull(skipped);
        Assert.Equal(
            new[] { new TextInjector.SkippedFormat(delayed, TextInjector.SkipReason.NotRetrievable), new TextInjector.SkippedFormat(0xC0DE, TextInjector.SkipReason.NotRetrievable) }.OrderBy(x => x.Format),
            skipped!.OrderBy(x => x.Format));
        var ids = new[] { delayed, 0xC0DEu }.Order().ToList();
        Assert.Equal($"{ids[0]}(not_retrievable),{ids[1]}(not_retrievable)", TextInjector.FormatSkipped(skipped!.OrderBy(x => x.Format).ToList()));
    }

    [Fact]
    public void FormatThatRendersOnSecondRequest_IsRetriedOnceAndRestored()
    {
        var late = Win32.RegisterClipboardFormatW("Whisparrot.Test.Late");
        var lateBytes = Utf16("遅れて描画");
        using var owner = new DelayedRenderOwner(ThreeFormats(), delayedFormats: [late], renderOnSecondRequest: new() { [late] = lateBytes });
        IReadOnlyList<TextInjector.SkippedFormat>? skipped = null;
        var r = TextInjector.InjectClipboard("投入テキスト", () => true, s => skipped = s);
        Assert.True(r.Outcome == InjectOutcome.Success, r.Message);
        Assert.Null(skipped);
        AssertRestored([.. ThreeFormats(), (late, lateBytes)], Win32.ReadAll());
    }

    [Fact]
    public void BitmapOnlyClipboard_Succeeds_AndBitmapIsStillAvailable()
    {
        var hbm = Win32.CreateBitmap(2, 2, 1, 32, IntPtr.Zero);
        Win32.SetHandle(CF_BITMAP, hbm);
        IReadOnlyList<TextInjector.SkippedFormat>? skipped = null;
        var r = TextInjector.InjectClipboard("投入テキスト", () => true, s => skipped = s);
        Assert.True(r.Outcome == InjectOutcome.Success, r.Message);
        Assert.Null(skipped); // CF_BITMAP は DIB からの読み替えで、飛ばした形式ではない
        // CF_DIB は OS が合成し、退避・復元の対象。CF_BITMAP は復元後に OS が再合成する。
        Assert.True(Win32.IsClipboardFormatAvailable(CF_BITMAP));
        Assert.True(Win32.IsClipboardFormatAvailable(8 /* CF_DIB */));
    }

    [Fact]
    public void EnhMetafileClipboard_Succeeds_AndOtherFormatsAreRestored()
    {
        var text = (CF_UNICODETEXT, Utf16("元のテキスト"));
        Win32.Set([text], enhMetafile: Win32.MakeEnhMetafile());
        IReadOnlyList<TextInjector.SkippedFormat>? skipped = null;
        var r = TextInjector.InjectClipboard("投入テキスト", () => true, s => skipped = s);
        Assert.True(r.Outcome == InjectOutcome.Success, r.Message);
        AssertRestored([text], Win32.ReadAll());
        Assert.NotNull(skipped);
        Assert.Contains(skipped!, x => x.Format == CF_ENHMETAFILE && x.Reason == TextInjector.SkipReason.GdiHandle);
        Assert.Contains("14:CF_ENHMETAFILE(gdi_handle)", TextInjector.FormatSkipped(skipped!));
    }
}

/// <summary>テスト用の Win32 補助（実クリップボードの設定・読み出し）。</summary>
internal static class Win32
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterClipboardFormatW(string name);
    [DllImport("user32.dll")] internal static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("gdi32.dll")] internal static extern IntPtr CreateBitmap(int w, int h, uint planes, uint bits, IntPtr data);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateEnhMetaFileW(IntPtr hdc, IntPtr file, IntPtr rect, IntPtr desc);
    [DllImport("gdi32.dll")] private static extern bool Rectangle(IntPtr hdc, int l, int t, int r, int b);
    [DllImport("gdi32.dll")] private static extern IntPtr CloseEnhMetaFile(IntPtr hdc);

    internal static IntPtr MakeEnhMetafile()
    {
        var dc = CreateEnhMetaFileW(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Rectangle(dc, 0, 0, 10, 10);
        return CloseEnhMetaFile(dc);
    }

    internal static void Open()
    {
        for (var i = 0; i < 100; i++)
        {
            if (OpenClipboard(IntPtr.Zero)) return;
            Thread.Sleep(20);
        }
        throw new InvalidOperationException("cannot open clipboard (non-interactive window station?)");
    }

    internal static bool SetGlobal(uint format, byte[] data)
    {
        var h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)data.Length);
        var p = GlobalLock(h);
        Marshal.Copy(data, 0, p, data.Length);
        GlobalUnlock(h);
        return SetClipboardData(format, h) != IntPtr.Zero;
    }

    /// <summary>クリップボードを空にして、HGLOBAL 形式（と任意の拡張メタファイル）を載せる。所有者なし。</summary>
    internal static void Set((uint Format, byte[] Data)[] formats, IntPtr enhMetafile = default)
    {
        Open();
        try
        {
            EmptyClipboard();
            foreach (var (f, d) in formats) Assert.True(SetGlobal(f, d));
            if (enhMetafile != IntPtr.Zero) Assert.NotEqual(IntPtr.Zero, SetClipboardData(14, enhMetafile));
        }
        finally { CloseClipboard(); }
    }

    internal static void SetHandle(uint format, IntPtr handle)
    {
        Open();
        try { EmptyClipboard(); Assert.NotEqual(IntPtr.Zero, SetClipboardData(format, handle)); }
        finally { CloseClipboard(); }
    }

    /// <summary>HGLOBAL として読める形式をすべて読む（GDI ハンドル形式・取得できない形式は含めない）。</summary>
    internal static Dictionary<uint, byte[]> ReadAll()
    {
        var map = new Dictionary<uint, byte[]>();
        Open();
        try
        {
            for (var f = EnumClipboardFormats(0); f != 0; f = EnumClipboardFormats(f))
            {
                if (f == 2 || f == 3 || f == 9 || f == 14 || (f >= 0x80 && f <= 0x8F)) continue;
                var h = GetClipboardData(f);
                if (h == IntPtr.Zero) continue;
                var size = (int)GlobalSize(h);
                var p = GlobalLock(h);
                if (size <= 0 || p == IntPtr.Zero) continue;
                var b = new byte[size];
                Marshal.Copy(p, b, 0, size);
                GlobalUnlock(h);
                map[f] = b;
            }
        }
        finally { CloseClipboard(); }
        return map;
    }

    internal static string? ReadUnicodeText()
    {
        var all = ReadAll();
        return all.TryGetValue(CF_UNICODETEXT, out var b) ? Encoding.Unicode.GetString(b).TrimEnd('\0') : null;
    }
}

/// <summary>
/// 遅延描画の所有者ウィンドウ。専用スレッドでメッセージを回し、WM_RENDERFORMAT / WM_RENDERALLFORMATS には
/// 何も供給せずに応答する（= 描画しない所有者）。生成時にクリップボードを所有して、指定形式を載せる。
/// </summary>
internal sealed class DelayedRenderOwner : IDisposable
{
    private const uint WM_DESTROY = 0x2, WM_CLOSE = 0x10, WM_RENDERFORMAT = 0x305, WM_RENDERALLFORMATS = 0x306;
    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSW
    {
        public uint style; public WndProc lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName; public string lpszClassName;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassW(ref WNDCLASSW c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClassW(string name, IntPtr hInstance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern int GetMessageW(out MSG m, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool DispatchMessageW(ref MSG m);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly WndProc _proc; // GC されないよう保持
    private readonly string _class = "WhisparrotTestOwner" + Guid.NewGuid().ToString("N");
    private IntPtr _hwnd;
    private Exception? _error;
    private int _renderRequests;

    public int RenderRequests => Volatile.Read(ref _renderRequests);

    private readonly Dictionary<uint, byte[]>? _renderOnSecond;
    private readonly Dictionary<uint, int> _requestsByFormat = new();

    public DelayedRenderOwner((uint Format, byte[] Data)[] realFormats, uint[] delayedFormats, Dictionary<uint, byte[]>? renderOnSecondRequest = null)
    {
        _renderOnSecond = renderOnSecondRequest;
        _proc = OnMessage;
        _thread = new Thread(() => Run(realFormats, delayedFormats)) { IsBackground = true, Name = "test-clipboard-owner" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
        if (_error != null) throw new InvalidOperationException("owner setup failed", _error);
    }

    private IntPtr OnMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
    {
        switch (msg)
        {
            case WM_RENDERFORMAT:
                Interlocked.Increment(ref _renderRequests);
                var f = (uint)(long)w;
                _requestsByFormat[f] = _requestsByFormat.GetValueOrDefault(f) + 1;
                // 要求側がクリップボードを開いているので、そのまま SetClipboardData で供給できる。
                if (_renderOnSecond != null && _requestsByFormat[f] >= 2 && _renderOnSecond.TryGetValue(f, out var data)) Win32.SetGlobal(f, data);
                return IntPtr.Zero;
            case WM_RENDERALLFORMATS:
                Interlocked.Increment(ref _renderRequests);
                return IntPtr.Zero; // 何も供給しない
            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, w, l);
    }

    private void Run((uint Format, byte[] Data)[] realFormats, uint[] delayedFormats)
    {
        try
        {
            var inst = GetModuleHandleW(null);
            var wc = new WNDCLASSW { lpfnWndProc = _proc, hInstance = inst, lpszClassName = _class };
            if (RegisterClassW(ref wc) == 0) throw new InvalidOperationException("RegisterClass failed");
            _hwnd = CreateWindowExW(0, _class, "owner", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, inst, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("CreateWindow failed");

            for (var i = 0; ; i++)
            {
                if (OpenClipboard(_hwnd)) break;
                if (i > 100) throw new InvalidOperationException("cannot open clipboard (non-interactive window station?)");
                Thread.Sleep(20);
            }
            try
            {
                EmptyClipboard(); // 所有者になる
                foreach (var (f, d) in realFormats) Win32.SetGlobal(f, d);
                foreach (var f in delayedFormats) SetClipboardData(f, IntPtr.Zero); // 遅延描画の登録
            }
            finally { CloseClipboard(); }
        }
        catch (Exception ex) { _error = ex; }
        finally { _ready.Set(); }
        if (_error != null) return;

        while (GetMessageW(out var m, IntPtr.Zero, 0, 0) > 0) DispatchMessageW(ref m);
        UnregisterClassW(_class, GetModuleHandleW(null));
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero) PostMessageW(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(5));
    }
}
