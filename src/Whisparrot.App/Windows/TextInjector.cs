using System.Runtime.InteropServices;
using Whisparrot.Core;
using Whisparrot.Core.Settings;
using static Whisparrot.App.Windows.NativeMethods;

namespace Whisparrot.App.Windows;

/// <summary>
/// クリップボード方式の投入（設計 §10.1）。元の内容を退避 → テキスト設定 → 貼り付け → 復元。
/// 貼り付けの経路は、投入の直前に解決したフォーカス中コントロールのクラスで振り分ける。標準のテキスト編集クラス（Edit / RichEdit 系 / Scintilla）は
/// WM_PASTE を同期送信する（キーボードフックを通らないため、キーリマッパの再定義に左右されない）。それ以外（GPUI 等のカスタム描画は、
/// 最上位ウィンドウへ WM_PASTE が届いても無視されるため）は SendInput（Ctrl+V）が主経路。標準クラスで宛先が得られない・送信が失敗／タイムアウトした場合、
/// および宛先が全く解決できない場合は、SendInput へ落ちる。
/// 経路は「アプリ別上書き（プロセス名）→ auto のクラス判定」の順で決める。auto 以外の経路（paste-message / ctrl-v / shift-insert / typing / custom）も選べる。
/// 投入先は録音開始時に記録でき（<see cref="CaptureTarget"/>）、その場合は途中でフォーカスが移っても記録した対象へ入れる:
/// WM_PASTE は記録したコントロールへ直接送り（前面化しない）、キー系とタイピングは投入直前に記録したウィンドウを前面化してから送る（前面化できなければ送らず失敗）。
/// typing はクリップボードを使わず、Unicode 文字入力（KEYEVENTF_UNICODE）で送る。改行は Enter キーではなく改行文字の入力として送る。
/// Enter キーは送らない。クリップボードは STA の専用スレッドで扱う。
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

    internal enum PasteStrategy { Auto, PasteMessage, CtrlV, ShiftInsert, Typing, Custom }
    internal enum MessageResult { Delivered, NoTarget, Failed }

    internal static class Routes
    {
        public const string PasteMessage = "paste-message";
        public const string CtrlV = "ctrl-v";
        public const string ShiftInsert = "shift-insert";
        public const string Typing = "typing";
        public const string Custom = "custom";
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

    /// <summary>診断用の強制指定。設定の経路名と同じ名前に加え、旧名 send-input（= ctrl-v）を受け付ける。</summary>
    internal static bool TryParseStrategy(string? value, out PasteStrategy strategy)
    {
        strategy = PasteStrategy.Auto;
        if (value == "send-input") { strategy = PasteStrategy.CtrlV; return true; }
        if (value == "auto" || !InsertionRoutes.TryParse(value, out var route)) return false; // auto は指定なし（既定）
        strategy = FromRoute(route);
        return true;
    }

    internal static PasteStrategy FromRoute(InsertionRoute route) => route switch
    {
        InsertionRoute.PasteMessage => PasteStrategy.PasteMessage,
        InsertionRoute.CtrlV => PasteStrategy.CtrlV,
        InsertionRoute.ShiftInsert => PasteStrategy.ShiftInsert,
        InsertionRoute.Typing => PasteStrategy.Typing,
        InsertionRoute.Custom => PasteStrategy.Custom,
        _ => PasteStrategy.Auto,
    };

    internal static string StrategyName(PasteStrategy s) => s switch
    {
        PasteStrategy.PasteMessage => Routes.PasteMessage, PasteStrategy.CtrlV => Routes.CtrlV, PasteStrategy.ShiftInsert => Routes.ShiftInsert,
        PasteStrategy.Typing => Routes.Typing, PasteStrategy.Custom => Routes.Custom, _ => "auto",
    };

    /// <summary>
    /// 経路の決定: 強制指定（診断）→ アプリ別上書き（プロセス名）→ auto。対象のプロセス名は記録した投入先から取る。
    /// </summary>
    internal static PasteStrategy DecideStrategy(PasteStrategy forced, InsertionSettings insertion, string? process)
    {
        if (forced != PasteStrategy.Auto) return forced;
        return insertion.OverrideFor(process) is { } r ? FromRoute(r) : PasteStrategy.Auto;
    }

    private readonly Func<InsertionSettings> _insertion;

    public TextInjector(Func<InsertionSettings>? insertion = null)
    {
        _insertion = insertion ?? (() => AppSettings.Default.Insertion);
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

    /// <summary>録音開始時に記録した投入先。Target は WM_PASTE の宛先（記録時点で解決済み）。</summary>
    internal sealed record RecordedTarget(PasteTarget Target) : IInjectTarget;

    /// <summary>タスクバー・通知領域など、入力先にならないシェルのウィンドウ。記録時は「有効な対象なし」とする（トレイ操作で録音を始めた場合など）。</summary>
    private static readonly string[] ShellClasses = ["Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland"];

    public IInjectTarget? CaptureTarget()
    {
        if (ResolveTarget() is not { } t || !IsExternalWindow(t.Foreground) || ShellClasses.Contains(t.ForegroundClass)) return null;
        return new RecordedTarget(t);
    }

    /// <summary>
    /// 記録した投入先を、今も使えるかを確認して返す。ウィンドウが無い・別プロセスのものになった（ハンドルの再利用）なら null。
    /// コントロールだけが無くなったときは、記録したウィンドウ内の編集コントロール（XAML アイランドの場合）か、ウィンドウ自体を宛先にする。
    /// </summary>
    internal static PasteTarget? ResolveRecorded(RecordedTarget r)
    {
        var t = r.Target;
        if (!IsWindow(t.Foreground) || GetWindowThreadProcessId(t.Foreground, out var pid) == 0 || pid != t.Pid) return null;
        if (IsWindow(t.Target) && GetWindowThreadProcessId(t.Target, out var tpid) != 0 && tpid == pid) return t;
        var edit = t.Hosted ? FindVisibleStandardEdit(t.Foreground) : null;
        return edit is { } e ? t with { Target = e.Hwnd } : t with { Target = t.Foreground };
    }

    public Task<InjectResult> InjectAsync(string text, CancellationToken ct) => InjectAsync(text, null, ct);

    public Task<InjectResult> InjectAsync(string text, IInjectTarget? target, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<InjectResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(InjectCore(text, target as RecordedTarget, PasteStrategy.Auto, null, null, null)); }
            catch (Exception ex) { tcs.SetResult(new InjectResult(InjectOutcome.Failed, $"入力に失敗しました: {ex.Message}")); }
        }) { IsBackground = true, Name = "whisparrot-inject" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private const string NoTargetMessage = "入力先のウィンドウが見つかりません";

    /// <summary>
    /// 投入の本体。recorded があれば記録した投入先へ、無ければ投入時点の前景へ入れる。経路は forced（診断の強制指定）→ アプリ別上書き → auto。
    /// 宛先が使えなければ何も送らずクリップボードにも触れず失敗する。
    /// </summary>
    internal InjectResult InjectCore(string text, RecordedTarget? recorded, PasteStrategy forced, Action<string>? trace, TimeSpan? grace, TimeSpan? messageGrace)
    {
        Func<PasteTarget?> resolve = recorded is null ? ResolveTarget : () => ResolveRecorded(recorded);
        if (resolve() is not { } t0 || !IsExternalWindow(t0.Foreground)) return Fail(NoTargetMessage);
        if (!WaitModifiersReleased()) return Fail("修飾キーが押されたままです");

        var insertion = _insertion();
        // 修飾キー待ちの間に変わりうる前景（従来動作）は、待ちの後で解決し直して経路を決める。
        if (resolve() is not { } t || !IsExternalWindow(t.Foreground)) return Fail(NoTargetMessage);
        var strategy = DecideStrategy(forced, insertion, t.Process);
        trace?.Invoke($"target_mode={(recorded is null ? "foreground" : "recorded")} process={t.Process} strategy={StrategyName(strategy)}");
        if (strategy == PasteStrategy.Typing) return TypeText(text, t, trace);
        return InjectClipboard(text, () => PasteToTarget(strategy, resolve, insertion, trace), grace: grace, trace: trace, messageGrace: messageGrace);
    }

    /// <summary>
    /// 貼り付け段の経路選択。Auto は宛先クラスで振り分ける: 標準編集クラス（または宛先未解決 = kind が null）は paste-message を先に試し、
    /// 宛先なし／失敗なら send-input へ落ちる。カスタム描画クラスは send-input だけ（WM_PASTE は届いても無視されうる）。
    /// 強制指定（診断用）は片方だけを使い、落ちない。メッセージ送信が時間切れになった場合、対象が後で処理して
    /// 予備経路と二重に貼り付ける可能性は残る（切り分けられない）。
    /// </summary>
    internal static PasteSend PasteStep(PasteStrategy strategy, Func<MessageResult> sendMessage, Func<bool> sendKeys, Action<string>? trace = null, TargetKind? kind = null)
    {
        var keyRoute = strategy switch { PasteStrategy.ShiftInsert => Routes.ShiftInsert, PasteStrategy.Custom => Routes.Custom, _ => Routes.CtrlV };
        var viaMessage = strategy == PasteStrategy.PasteMessage || (strategy == PasteStrategy.Auto && kind != TargetKind.CustomDrawn);
        if (viaMessage)
        {
            var m = sendMessage();
            trace?.Invoke($"paste_message={m}");
            if (m == MessageResult.Delivered) return new PasteSend(true, Routes.PasteMessage);
            if (strategy == PasteStrategy.PasteMessage) return new PasteSend(false, Routes.PasteMessage);
            trace?.Invoke("fallback=ctrl-v");
        }
        else if (strategy == PasteStrategy.Auto) trace?.Invoke("route_by_class=ctrl-v");
        return new PasteSend(sendKeys(), keyRoute);
    }

    /// <summary>
    /// 投入の直前（クリップボードへ設定した後）に宛先を解決して貼り付ける。解決結果が自プロセス・シェル・デスクトップなら何も送らず中止する。
    /// 宛先が全く得られないときは従来どおりキー（Ctrl+V）へ落ちる。キー系は、送る前に宛先のウィンドウを前面化する（できなければ送らず失敗）。
    /// </summary>
    private static PasteSend PasteToTarget(PasteStrategy strategy, Func<PasteTarget?> resolve, InsertionSettings insertion, Action<string>? trace)
    {
        var target = resolve();
        TargetKind? kind = null;
        if (target is { } t)
        {
            if (!IsExternalWindow(t.Foreground)) return new PasteSend(false, Routes.None, NoTargetMessage);
            kind = Classify(t.TargetClass);
            trace?.Invoke($"target process={t.Process} foreground_class={t.ForegroundClass} focus={(t.HasFocusControl ? t.FocusClass : "none")} target_class={t.TargetClass} hosted_editor={t.Hosted} kind={kind}");
        }
        else trace?.Invoke("target=none");

        string? keyError = null;
        bool SendKeys()
        {
            if (strategy == PasteStrategy.Custom && insertion.CustomKey is null) { keyError = "カスタムキーが設定されていません"; return false; }
            if (target is { } x && !BringToForeground(x, trace)) { keyError = FrontFailedMessage; return false; }
            var (mods, vk) = strategy switch
            {
                PasteStrategy.ShiftInsert => (HotkeyModifiers.Shift, (uint)VK_INSERT),
                PasteStrategy.Custom => (insertion.CustomKey!.Modifiers, insertion.CustomKey.VirtualKey),
                _ => (HotkeyModifiers.Control, (uint)VK_V),
            };
            return SendChord(mods, vk);
        }

        var r = PasteStep(strategy, () => target is { } x ? SendPasteMessageTo(x.Target) : MessageResult.NoTarget, SendKeys, trace, kind);
        if (!r.Sent && keyError != null) r = r with { Error = keyError };
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
    internal readonly record struct PasteTarget(IntPtr Foreground, IntPtr Focus, IntPtr Target, string ForegroundClass, string FocusClass, string TargetClass, string Process, bool Hosted, uint Pid = 0)
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
        return new PasteTarget(fg, focus, target, fgClass, focusClass, targetClass, process, hosted, pid);
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
        InjectClipboard(text, () => new PasteSend(sendPaste(), Routes.CtrlV), onSkipped, grace, trace);

    /// <summary>
    /// 本体。復元前の待ちは経路で変わる: キー系（ctrl-v / shift-insert / custom）は非同期に読まれうるので grace（既定 PasteGrace）、
    /// paste-message は同期送信なので PasteMessageGrace（既定 0）。grace 引数はキー系の待ちを上書きする。
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
    internal static int InjectProbe(int? graceMs, PasteStrategy strategy, Action<string> write, int? messageGraceMs = null,
        InsertionSettings? insertion = null, int? fixedSwitchSeconds = null, string? text = null, int countdownSeconds = 5)
    {
        var grace = graceMs is { } g ? TimeSpan.FromMilliseconds(g) : PasteGrace;
        var messageGrace = messageGraceMs is { } mg ? TimeSpan.FromMilliseconds(mg) : PasteMessageGrace;
        text ??= ProbeText;
        insertion ??= AppSettings.Default.Insertion;
        write($"strategy={StrategyName(strategy)}");
        write($"app_routes={(insertion.AppRoutes.Count == 0 ? "none" : string.Join(",", insertion.AppRoutes.Select(a => $"{a.Process}={InsertionRoutes.Name(a.Route)}")))} custom_key={insertion.CustomKey?.Display ?? "none"}");
        write($"grace_ms={(int)grace.TotalMilliseconds} message_grace_ms={(int)messageGrace.TotalMilliseconds}");
        for (var i = countdownSeconds; i >= 1; i--)
        {
            write($"countdown={i} (focus the target app now)");
            Thread.Sleep(1000);
        }
        var injector = new TextInjector(() => insertion);
        var exit = 1;
        var thread = new Thread(() =>
        {
            RecordedTarget? recorded = null;
            if (fixedSwitchSeconds is { } sw)
            {
                // 開始時固定の確認: ここで投入先を記録し、その後の待ちの間に別アプリへ切り替えてもらう。
                recorded = injector.CaptureTarget() as RecordedTarget;
                write($"recorded={(recorded is null ? "none" : $"process={recorded.Target.Process} foreground_class={recorded.Target.ForegroundClass} target_class={recorded.Target.TargetClass}")}");
                for (var i = sw; i >= 1; i--)
                {
                    write($"switch_countdown={i} (switch to another app now)");
                    Thread.Sleep(1000);
                }
                write($"foreground_at_inject={DescribeForeground()}");
            }
            // 固定時の foreground / paste_target は「今の前景」の説明（投入先は recorded 行と target_mode 行）。
            var now = recorded is null ? "" : "current_";
            write($"{now}foreground={DescribeForeground()}");
            write($"{now}paste_target={DescribeTarget()}");
            write($"inject_text_length={text.Length}");
            var r = injector.InjectCore(text, recorded, strategy, write, grace, messageGrace);
            write($"foreground_after={DescribeForeground()}");
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

    private const int VK_INSERT = 0x2D;

    private static INPUT KeyInput(ushort vk, bool up, bool extended = false) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = (up ? KEYEVENTF_KEYUP : 0) | (extended ? KEYEVENTF_EXTENDEDKEY : 0) } },
    };

    /// <summary>ナビゲーション系のキーは拡張キーとして送る（テンキーの同名キーと区別するため）。</summary>
    private static bool IsExtendedKey(uint vk) => vk is >= 0x21 and <= 0x28 or 0x2D or 0x2E;

    /// <summary>修飾キーの押下 → キーの押下・解放 → 修飾キーの解放（逆順）。順序は Ctrl, Alt, Shift, Win。</summary>
    internal static INPUT[] BuildChordInputs(HotkeyModifiers mods, uint vk)
    {
        var mk = new List<ushort>();
        if (mods.HasFlag(HotkeyModifiers.Control)) mk.Add(VK_CONTROL);
        if (mods.HasFlag(HotkeyModifiers.Alt)) mk.Add(VK_MENU);
        if (mods.HasFlag(HotkeyModifiers.Shift)) mk.Add(VK_SHIFT);
        if (mods.HasFlag(HotkeyModifiers.Win)) mk.Add(VK_LWIN);
        var inputs = new List<INPUT>();
        foreach (var m in mk) inputs.Add(KeyInput(m, false));
        inputs.Add(KeyInput((ushort)vk, false, IsExtendedKey(vk)));
        inputs.Add(KeyInput((ushort)vk, true, IsExtendedKey(vk)));
        for (var i = mk.Count - 1; i >= 0; i--) inputs.Add(KeyInput(mk[i], true));
        return inputs.ToArray();
    }

    private static bool SendChord(HotkeyModifiers mods, uint vk)
    {
        var inputs = BuildChordInputs(mods, vk);
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length;
    }

    // ---- typing（Unicode 文字入力）----

    /// <summary>SendInput 1 回あたりの UTF-16 コード単位数（1 単位 = 押下 + 解放の 2 イベント）。まとめて送りつつ、入力キューを溢れさせない大きさ。</summary>
    internal const int TypingChunkUnits = 256;

    /// <summary>
    /// 入力する文字列へ整える。改行（CRLF / CR / LF）は LF 1 文字にそろえる（Enter キーではなく改行文字として送るため）。
    /// タブ以外の制御文字は入力しない。
    /// </summary>
    internal static string NormalizeTypingText(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r') { sb.Append('\n'); if (i + 1 < text.Length && text[i + 1] == '\n') i++; }
            else if (c == '\n' || c == '\t' || !char.IsControl(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>各コード単位を KEYEVENTF_UNICODE の押下 + 解放の組にする（wVk = 0、wScan = コード単位）。サロゲートペアは 2 単位として続けて送る。</summary>
    internal static INPUT[] BuildTypingInputs(string normalized)
    {
        var inputs = new INPUT[normalized.Length * 2];
        for (var i = 0; i < normalized.Length; i++)
        {
            inputs[2 * i] = UnicodeInput(normalized[i], false);
            inputs[2 * i + 1] = UnicodeInput(normalized[i], true);
        }
        return inputs;
    }

    private static INPUT UnicodeInput(char unit, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = unit, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0) } },
    };

    /// <summary>[start, start + n) のうち、サロゲートペアを分けないようにした区切り位置の列。</summary>
    internal static IEnumerable<(int Start, int Length)> TypingChunks(string normalized, int chunkUnits = TypingChunkUnits)
    {
        var pos = 0;
        while (pos < normalized.Length)
        {
            var len = Math.Min(chunkUnits, normalized.Length - pos);
            if (pos + len < normalized.Length && char.IsHighSurrogate(normalized[pos + len - 1])) len++;
            yield return (pos, len);
            pos += len;
        }
    }

    private const string FrontFailedMessage = "入力先のウィンドウを前面にできなかったため、入力しませんでした";

    /// <summary>
    /// typing 経路: クリップボードを使わず、Unicode 文字入力で送る（退避・復元なし）。記録したウィンドウを前面化し（できなければ送らず失敗）、
    /// チャンクごとに前面が変わっていないことを確かめてからまとめて SendInput する。
    /// </summary>
    private static InjectResult TypeText(string text, PasteTarget target, Action<string>? trace)
    {
        var norm = NormalizeTypingText(text);
        trace?.Invoke($"typing_units={norm.Length} chunks={TypingChunks(norm).Count()}");
        if (!BringToForeground(target, trace)) return new InjectResult(InjectOutcome.Failed, FrontFailedMessage, Routes.Typing, target.TargetClass);
        foreach (var (start, length) in TypingChunks(norm))
        {
            if (GetForegroundWindow() != target.Foreground)
                return new InjectResult(InjectOutcome.Failed, "入力の途中で前面のウィンドウが変わったため、中断しました", Routes.Typing, target.TargetClass);
            var inputs = BuildTypingInputs(norm.Substring(start, length));
            if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length)
                return new InjectResult(InjectOutcome.Failed, "文字入力を送れませんでした", Routes.Typing, target.TargetClass);
        }
        trace?.Invoke("typing_sent=True");
        return new InjectResult(InjectOutcome.Success, Route: Routes.Typing, TargetClass: target.TargetClass);
    }

    // ---- 前面化 ----

    internal static readonly TimeSpan ForegroundWait = TimeSpan.FromMilliseconds(500);
    internal static readonly TimeSpan FocusSettleWait = TimeSpan.FromMilliseconds(500);
    internal static readonly TimeSpan FocusSettleMin = TimeSpan.FromMilliseconds(40);

    /// <summary>
    /// 記録したウィンドウを前面にする。すでに前面なら何もしない。バックグラウンドからの SetForegroundWindow は拒否されうるので、
    /// まず直接、だめなら現在の前面スレッドの入力へ一時的に接続して再試行する。最後に前面を確認し、前面にできなかったら false
    /// （呼び出し側は何も送らない）。前面化したときは、対象のスレッドがフォーカスを復元するまで少し待つ。
    /// </summary>
    internal static bool BringToForeground(PasteTarget target, Action<string>? trace)
    {
        var hwnd = target.Foreground;
        if (GetForegroundWindow() == hwnd) { trace?.Invoke("foreground=already"); return true; }
        if (!IsWindow(hwnd)) return false;
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);

        SetForegroundWindow(hwnd);
        var ok = WaitForeground(hwnd, ForegroundWait / 2);
        var how = "direct";
        if (!ok)
        {
            how = "attach";
            var current = GetForegroundWindow();
            var currentThread = current != IntPtr.Zero ? GetWindowThreadProcessId(current, out _) : 0;
            var me = GetCurrentThreadId();
            var attached = currentThread != 0 && currentThread != me && AttachThreadInput(me, currentThread, true);
            try
            {
                BringWindowToTop(hwnd);
                SetForegroundWindow(hwnd);
            }
            finally { if (attached) AttachThreadInput(me, currentThread, false); }
            ok = WaitForeground(hwnd, ForegroundWait);
        }
        trace?.Invoke($"foreground={(ok ? "brought" : "failed")} how={how}");
        if (!ok) return false;

        // 前面化の直後は、対象スレッドがまだフォーカスを復元していないことがある。復元されるまで（上限つきで）待つ。
        var tid = GetWindowThreadProcessId(hwnd, out _);
        var start = DateTime.UtcNow;
        while (true)
        {
            var elapsed = DateTime.UtcNow - start;
            var info = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>() };
            var hasFocus = tid != 0 && GetGUIThreadInfo(tid, ref info) && info.hwndFocus != IntPtr.Zero;
            if (elapsed >= FocusSettleMin && hasFocus) break;
            if (elapsed >= FocusSettleWait) break;
            Thread.Sleep(10);
        }
        return GetForegroundWindow() == hwnd;
    }

    private static bool WaitForeground(IntPtr hwnd, TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        while (true)
        {
            if (GetForegroundWindow() == hwnd) return true;
            if (DateTime.UtcNow >= deadline) return false;
            Thread.Sleep(10);
        }
    }
}
