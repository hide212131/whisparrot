using System.Runtime.InteropServices;
using Whisparrot.Core;
using static Whisparrot.App.Windows.NativeMethods;

namespace Whisparrot.App.Windows;

/// <summary>
/// クリップボード方式の投入（設計 §10.1）。元の内容を退避 → テキスト設定 → 貼り付け → 復元。
/// 貼り付けの経路は、投入の直前に解決したフォーカス中コントロールのクラスで振り分ける。標準のテキスト編集クラス（Edit / RichEdit 系 / Scintilla）は
/// WM_PASTE を同期送信する（キーボードフックを通らないため、キーリマッパの再定義に左右されない）。それ以外（GPUI 等のカスタム描画は、
/// 最上位ウィンドウへ WM_PASTE が届いても無視されるため）は SendInput（Ctrl+V）が主経路。標準クラスで宛先が得られない・送信が失敗／タイムアウトした場合、
/// および宛先が全く解決できない場合は、SendInput へ落ちる。
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

    /// <summary>WM_PASTE の送信が戻らないときに待つ上限。超えたら失敗とみなし予備経路へ落ちる。</summary>
    internal static readonly TimeSpan PasteMessageTimeout = TimeSpan.FromSeconds(2);

    /// <summary>WM_PASTE は同期送信で、対象は処理中にクリップボードを読むため、戻り後の待ちは既定で不要（0）。実測で必要なら根拠を添えて増やす。</summary>
    internal static readonly TimeSpan PasteMessageGrace = TimeSpan.Zero;

    internal enum PasteStrategy { Auto, PasteMessage, SendInput }
    internal enum MessageResult { Delivered, NoTarget, Failed }

    internal static class Routes
    {
        public const string PasteMessage = "paste-message";
        public const string SendInput = "send-input";
        public const string None = "none";
    }

    /// <summary>貼り付け段の結果。Route は実際に送信を試みた最後の経路。Error があれば何も送らずに中止した理由（利用者向け文言）。</summary>
    internal readonly record struct PasteSend(bool Sent, string Route, string? Error = null, string? TargetClass = null);

    internal enum TargetKind { StandardEdit, CustomDrawn }

    /// <summary>
    /// WM_PASTE で確実に貼り付けられる標準のテキスト編集クラス。実測で確認したのは Edit と RichEdit 系（メモ帳の RichEditD2DPT を含む）。
    /// Scintilla は WM_PASTE を実装する広く使われる編集コントロールとして加えた。これ以外はカスタム描画とみなす。
    /// </summary>
    internal static TargetKind Classify(string? windowClass)
    {
        if (string.IsNullOrEmpty(windowClass)) return TargetKind.CustomDrawn;
        if (windowClass.Equals("Edit", StringComparison.OrdinalIgnoreCase)
            || windowClass.Equals("Scintilla", StringComparison.OrdinalIgnoreCase)
            || windowClass.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase)) return TargetKind.StandardEdit;
        return TargetKind.CustomDrawn;
    }

    internal static bool TryParseStrategy(string? value, out PasteStrategy strategy)
    {
        strategy = PasteStrategy.Auto;
        switch (value)
        {
            case "paste-message": strategy = PasteStrategy.PasteMessage; return true;
            case "send-input": strategy = PasteStrategy.SendInput; return true;
            default: return false;
        }
    }

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

        return InjectClipboard(text, () => PasteToForeground(PasteStrategy.Auto, null));
    }

    /// <summary>
    /// 貼り付け段の経路選択。Auto は宛先クラスで振り分ける: 標準編集クラス（または宛先未解決 = kind が null）は paste-message を先に試し、
    /// 宛先なし／失敗なら send-input へ落ちる。カスタム描画クラスは send-input だけ（WM_PASTE は届いても無視されうる）。
    /// 強制指定（診断用）は片方だけを使い、落ちない。メッセージ送信が時間切れになった場合、対象が後で処理して
    /// 予備経路と二重に貼り付ける可能性は残る（切り分けられない）。
    /// </summary>
    internal static PasteSend PasteStep(PasteStrategy strategy, Func<MessageResult> sendMessage, Func<bool> sendKeys, Action<string>? trace = null, TargetKind? kind = null)
    {
        var viaMessage = strategy == PasteStrategy.PasteMessage || (strategy == PasteStrategy.Auto && kind != TargetKind.CustomDrawn);
        if (viaMessage)
        {
            var m = sendMessage();
            trace?.Invoke($"paste_message={m}");
            if (m == MessageResult.Delivered) return new PasteSend(true, Routes.PasteMessage);
            if (strategy == PasteStrategy.PasteMessage) return new PasteSend(false, Routes.PasteMessage);
            trace?.Invoke("fallback=send-input");
        }
        else if (strategy == PasteStrategy.Auto) trace?.Invoke("route_by_class=send-input");
        return new PasteSend(sendKeys(), Routes.SendInput);
    }

    /// <summary>
    /// 投入の直前（クリップボードへ設定した後）に宛先を解決して貼り付ける。解決結果が自プロセス・シェル・デスクトップなら何も送らず中止する。
    /// 宛先が全く得られないときは従来どおり send-input へ落ちる。
    /// </summary>
    private static PasteSend PasteToForeground(PasteStrategy strategy, Action<string>? trace)
    {
        var target = ResolveTarget();
        TargetKind? kind = null;
        if (target is { } t)
        {
            if (!IsExternalWindow(t.Foreground)) return new PasteSend(false, Routes.None, "入力先のウィンドウが見つかりません");
            kind = Classify(t.TargetClass);
            trace?.Invoke($"target process={t.Process} foreground_class={t.ForegroundClass} focus={(t.HasFocusControl ? t.FocusClass : "none")} target_class={t.TargetClass} hosted_editor={t.Hosted} kind={kind}");
        }
        else trace?.Invoke("target=none");
        var r = PasteStep(strategy, () => target is { } x ? SendPasteMessageTo(x.Target) : MessageResult.NoTarget, SendCtrlV, trace, kind);
        return r with { TargetClass = target?.TargetClass };
    }

    internal static MessageResult SendPasteMessageTo(IntPtr hwnd)
    {
        var r = SendMessageTimeoutW(hwnd, WM_PASTE, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, (uint)PasteMessageTimeout.TotalMilliseconds, out _);
        return r == IntPtr.Zero ? MessageResult.Failed : MessageResult.Delivered;
    }

    /// <summary>
    /// 投入直前に解決した宛先。Focus は前景スレッドのフォーカス中コントロール（無ければ Zero）。Target は WM_PASTE の宛先で、通常は Focus（無ければ前景ウィンドウ）。
    /// Focus が XAML アイランドの入力サイト（<see cref="InputSiteClass"/>）のときは、実際の編集コントロールがその外側のウィンドウ木にあるので、
    /// 前景ウィンドウ内で可視の標準編集コントロールがちょうど 1 つだけなら Target をそれにする（Hosted = true）。
    /// </summary>
    internal readonly record struct PasteTarget(IntPtr Foreground, IntPtr Focus, IntPtr Target, string ForegroundClass, string FocusClass, string TargetClass, string Process, bool Hosted)
    {
        public bool HasFocusControl => Focus != IntPtr.Zero;
    }

    /// <summary>
    /// WinUI / XAML アイランドの入力サイト。新しいメモ帳では、キー入力の受け手が実際には内側の RichEdit でも、Win32 のフォーカスがこのクラスに見える状態があり
    /// （実測: 新規ウィンドウ直後は常に、その後も交互に）、このとき WM_PASTE を送っても無視される。
    /// </summary>
    internal const string InputSiteClass = "InputSiteWindowClass";

    private static PasteTarget? ResolveTarget()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return null;
        var tid = GetWindowThreadProcessId(fg, out var pid);
        var info = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
        var focus = tid != 0 && GetGUIThreadInfo(tid, ref info) ? info.hwndFocus : IntPtr.Zero;
        string process;
        try { process = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
        catch { process = "unknown"; }
        var fgClass = ClassOf(fg);
        var focusClass = focus != IntPtr.Zero ? ClassOf(focus) : "";
        var target = focus != IntPtr.Zero ? focus : fg;
        var targetClass = focus != IntPtr.Zero ? focusClass : fgClass;
        var hosted = false;
        if (focusClass == InputSiteClass && FindVisibleStandardEdit(fg) is { } edit)
        {
            target = edit.Hwnd; targetClass = edit.Class; hosted = true;
        }
        return new PasteTarget(fg, focus, target, fgClass, focusClass, targetClass, process, hosted);
    }

    /// <summary>top の子孫のうち可視（親も可視）な標準編集コントロール。ちょうど 1 つのときだけ返す（複数・なしは曖昧なので null）。</summary>
    internal static (IntPtr Hwnd, string Class)? FindVisibleStandardEdit(IntPtr top, Func<IntPtr, bool>? isVisible = null)
    {
        isVisible ??= IsWindowVisible;
        var found = new List<(IntPtr, string)>();
        EnumChildWindows(top, (h, _) =>
        {
            if (isVisible(h) && ClassOf(h) is var c && Classify(c) == TargetKind.StandardEdit) found.Add((h, c));
            return true;
        }, IntPtr.Zero);
        return found.Count == 1 ? found[0] : null;
    }

    private static string ClassOf(IntPtr hwnd)
    {
        var cls = new System.Text.StringBuilder(256);
        GetClassNameW(hwnd, cls, cls.Capacity);
        return cls.ToString();
    }

    /// <summary>
    /// 前景確認・修飾キー待ちの後の本体: 退避 → テキスト設定 → 貼り付け送信 → 復元。
    /// 貼り付けの送信を差し替えられるようにしてあり、テストはキー入力なしでクリップボード側だけを検証する。
    /// </summary>
    internal static InjectResult InjectClipboard(string text, Func<bool> sendPaste, Action<IReadOnlyList<SkippedFormat>>? onSkipped = null,
        TimeSpan? grace = null, Action<string>? trace = null) =>
        InjectClipboard(text, () => new PasteSend(sendPaste(), Routes.SendInput), onSkipped, grace, trace);

    /// <summary>
    /// 本体。復元前の待ちは経路で変わる: send-input は非同期に読まれうるので grace（既定 PasteGrace）、
    /// paste-message は同期送信なので PasteMessageGrace（既定 0）。grace 引数は send-input の待ちを上書きする。
    /// </summary>
    internal static InjectResult InjectClipboard(string text, Func<PasteSend> paste, Action<IReadOnlyList<SkippedFormat>>? onSkipped = null,
        TimeSpan? grace = null, Action<string>? trace = null, TimeSpan? messageGrace = null)
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

        var pasted = paste();
        var (sent, route) = (pasted.Sent, pasted.Route);
        trace?.Invoke($"route={route}");
        trace?.Invoke($"paste_sent={sent}");
        var wait = pasted.Error != null ? TimeSpan.Zero : route == Routes.PasteMessage ? messageGrace ?? PasteMessageGrace : grace ?? PasteGrace;
        if (wait > TimeSpan.Zero) Thread.Sleep(wait);

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
        if (pasted.Error != null) return new InjectResult(InjectOutcome.Failed, pasted.Error);
        if (sent) return new InjectResult(InjectOutcome.Success, Route: route, TargetClass: pasted.TargetClass);
        return new InjectResult(InjectOutcome.Failed, route == Routes.PasteMessage ? "貼り付けメッセージを送れませんでした" : "キー入力を送れませんでした", route, pasted.TargetClass);
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
    internal static int InjectProbe(int? graceMs, PasteStrategy strategy, Action<string> write, int? messageGraceMs = null)
    {
        var grace = graceMs is { } g ? TimeSpan.FromMilliseconds(g) : PasteGrace;
        var messageGrace = messageGraceMs is { } mg ? TimeSpan.FromMilliseconds(mg) : PasteMessageGrace;
        write($"strategy={strategy switch { PasteStrategy.PasteMessage => Routes.PasteMessage, PasteStrategy.SendInput => Routes.SendInput, _ => "auto" }}");
        write($"grace_ms={(int)grace.TotalMilliseconds} message_grace_ms={(int)messageGrace.TotalMilliseconds}");
        for (var i = 5; i >= 1; i--)
        {
            write($"countdown={i} (focus the target app now)");
            Thread.Sleep(1000);
        }
        var exit = 1;
        var thread = new Thread(() =>
        {
            write($"foreground={DescribeForeground()}");
            write($"paste_target={DescribeTarget()}");
            var external = ForegroundIsExternal();
            write($"foreground_external={external}");
            if (!external) { write("result=Failed 入力先のウィンドウが見つかりません"); return; }
            if (!WaitModifiersReleased()) { write("result=Failed 修飾キーが押されたままです"); return; }
            write($"inject_text_length={ProbeText.Length}");
            var r = InjectClipboard(ProbeText, () => PasteToForeground(strategy, write),
                s => write($"skipped_formats={FormatSkipped(s)}"), grace, write, messageGrace);
            write($"route_used={r.Route ?? "none"}");
            write($"target_class_used={(string.IsNullOrEmpty(r.TargetClass) ? "none" : r.TargetClass)}");
            write($"result={r.Outcome}{(r.Message is { } m ? " " + m : "")}");
            if (r.Outcome == InjectOutcome.Success) exit = 0;
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return exit;
    }

    private static string DescribeForeground() =>
        ResolveTarget() is { } t ? $"process={t.Process} class={t.ForegroundClass}" : "none";

    /// <summary>診断用: フォーカス中コントロール（hwndFocus）の有無とクラス、宛先クラス。タイトルは出さない。</summary>
    private static string DescribeTarget() =>
        ResolveTarget() is { } t
            ? $"focus={(t.HasFocusControl ? "class=" + t.FocusClass : "none")} target_class={t.TargetClass} hosted_editor={t.Hosted} target_is_foreground={t.Target == t.Foreground} kind={Classify(t.TargetClass)}"
            : "none";

    private delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder name, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormatW(string name);

    private static InjectResult Fail(string message) => new(InjectOutcome.Failed, message);

    private static bool ForegroundIsExternal() => IsExternalWindow(GetForegroundWindow());

    private static bool IsExternalWindow(IntPtr hwnd)
    {
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
