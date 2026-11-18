using System.Diagnostics;
using System.Runtime.InteropServices;
using Whisparrot.App.Windows;
using Whisparrot.Core;
using static Whisparrot.App.Windows.NativeMethods;
using static Whisparrot.App.Windows.TextInjector;

namespace Whisparrot.Tests;

/// <summary>貼り付け段の経路選択（paste-message 主経路 / send-input 予備経路）と、WM_PASTE の実送信の検証。</summary>
[Collection("Clipboard")]
public sealed class PasteMessageTests
{
    private static byte[] Utf16(string s) => System.Text.Encoding.Unicode.GetBytes(s + "\0");

    [Fact]
    public void Auto_UsesPasteMessage_AndNeverSendsKeys_WhenDelivered()
    {
        var keys = 0;
        var r = PasteStep(PasteStrategy.Auto, () => MessageResult.Delivered, () => { keys++; return true; });
        Assert.Equal(new PasteSend(true, Routes.PasteMessage), r);
        Assert.Equal(0, keys);
    }

    [Theory]
    [InlineData(MessageResult.NoTarget)]
    [InlineData(MessageResult.Failed)]
    internal void Auto_FallsBackToSendInput_WhenNoTargetOrFailed(MessageResult m)
    {
        var keys = 0;
        var r = PasteStep(PasteStrategy.Auto, () => m, () => { keys++; return true; });
        Assert.Equal(new PasteSend(true, Routes.SendInput), r);
        Assert.Equal(1, keys);
    }

    [Fact]
    public void ForcedPasteMessage_DoesNotFallBack()
    {
        var keys = 0;
        var r = PasteStep(PasteStrategy.PasteMessage, () => MessageResult.Failed, () => { keys++; return true; });
        Assert.Equal(new PasteSend(false, Routes.PasteMessage), r);
        Assert.Equal(0, keys);
    }

    [Fact]
    public void ForcedSendInput_NeverSendsMessage()
    {
        var msgs = 0;
        var r = PasteStep(PasteStrategy.SendInput, () => { msgs++; return MessageResult.Delivered; }, () => true);
        Assert.Equal(new PasteSend(true, Routes.SendInput), r);
        Assert.Equal(0, msgs);
    }

    [Theory]
    [InlineData("paste-message", PasteStrategy.PasteMessage)]
    [InlineData("send-input", PasteStrategy.SendInput)]
    internal void ParseStrategy_AcceptsKnownNames(string s, PasteStrategy expected)
    {
        Assert.True(TryParseStrategy(s, out var got));
        Assert.Equal(expected, got);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("")]
    [InlineData(null)]
    public void ParseStrategy_RejectsOthers(string? s) => Assert.False(TryParseStrategy(s, out _));

    [Fact]
    public void MessageRoute_RestoresWithoutWaitingForGrace_AndReportsRoute()
    {
        Win32.Set([(CF_UNICODETEXT, Utf16("元のテキスト"))]);
        string? seen = null;
        var sw = Stopwatch.StartNew();
        var r = InjectClipboard("投入テキスト", () => { seen = Win32.ReadUnicodeText(); return new PasteSend(true, Routes.PasteMessage); }, grace: TimeSpan.FromSeconds(5));
        sw.Stop();
        Assert.Equal(InjectOutcome.Success, r.Outcome);
        Assert.Equal(Routes.PasteMessage, r.Route);
        Assert.Equal("投入テキスト", seen);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"waited {sw.Elapsed}");
        Assert.Equal("元のテキスト", Win32.ReadUnicodeText());
    }

    [Fact]
    public void SendInputRoute_StillWaitsForGrace()
    {
        Win32.Set([(CF_UNICODETEXT, Utf16("元のテキスト"))]);
        var sw = Stopwatch.StartNew();
        var r = InjectClipboard("投入テキスト", () => true, grace: TimeSpan.FromMilliseconds(400));
        sw.Stop();
        Assert.Equal(Routes.SendInput, r.Route);
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(350), $"waited only {sw.Elapsed}");
    }

    [Fact]
    public void FailedMessageRoute_ReportsFailureWithRoute_AndRestores()
    {
        Win32.Set([(CF_UNICODETEXT, Utf16("元のテキスト"))]);
        var r = InjectClipboard("投入テキスト", () => new PasteSend(false, Routes.PasteMessage));
        Assert.Equal(InjectOutcome.Failed, r.Outcome);
        Assert.Equal(Routes.PasteMessage, r.Route);
        Assert.Equal("元のテキスト", Win32.ReadUnicodeText());
    }

    /// <summary>実際の EDIT コントロール（別スレッドのメッセージループ）へ WM_PASTE を送り、貼り付けと即時復元を確認する。</summary>
    [Fact]
    public void RealWmPaste_IntoEditControl_PastesAndClipboardIsRestoredImmediately()
    {
        using var edit = new EditHost();
        Win32.Set([(CF_UNICODETEXT, Utf16("元のテキスト"))]);
        var r = InjectClipboard("投入テキスト", () => new PasteSend(SendPasteMessageTo(edit.Hwnd) == MessageResult.Delivered, Routes.PasteMessage));
        Assert.True(r.Outcome == InjectOutcome.Success, r.Message);
        Assert.Equal("投入テキスト", edit.Text());
        Assert.Equal("元のテキスト", Win32.ReadUnicodeText());
    }

    [Fact]
    public void WmPaste_ToInvalidWindow_IsNotDelivered()
    {
        Assert.NotEqual(MessageResult.Delivered, SendPasteMessageTo(new IntPtr(0x0BADBAD)));
    }

    private sealed class EditHost : IDisposable
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern int GetMessageW(out MSG m, IntPtr h, uint min, uint max);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG m);
        [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG m);
        [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint tid, uint msg, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr h, System.Text.StringBuilder sb, int max);

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

        private readonly Thread _thread;
        private uint _tid;
        public IntPtr Hwnd { get; private set; }

        public EditHost()
        {
            var ready = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                _tid = GetCurrentThreadId();
                Hwnd = CreateWindowExW(0, "EDIT", "", 0x80000000u /* WS_POPUP */, 0, 0, 100, 50, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                ready.Set();
                while (GetMessageW(out var m, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref m); DispatchMessageW(ref m); }
                if (Hwnd != IntPtr.Zero) DestroyWindow(Hwnd);
            }) { IsBackground = true };
            _thread.Start();
            Assert.True(ready.Wait(5000));
            Assert.NotEqual(IntPtr.Zero, Hwnd);
        }

        public string Text()
        {
            var sb = new System.Text.StringBuilder(256);
            GetWindowTextW(Hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public void Dispose()
        {
            PostThreadMessageW(_tid, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(2000);
        }
    }
}
