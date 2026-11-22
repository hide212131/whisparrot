using System.Diagnostics;
using Whisparrot.App.Windows;
using Whisparrot.Core.Hane;
using Whisparrot.Infrastructure.Hane;

namespace Whisparrot.App;

/// <summary>
/// Hane 連携（W-1）の非対話診断。
///   --hane-probe [--countdown 秒] [--context]
///        カウントダウン後の前景ウィンドウに対し、記述子の読取・パイプ接続・認証・版交渉・機能・処理時間を確認する。
///        --context を付けると、その窓の現在の文書への読取（context.get）を一度だけ試す。
/// 出力は可否・大きさ・エラーコードに限る。認識・外部送信・編集は行わず、文書のテキスト・タイトル・トークンは出さない。
/// </summary>
internal static partial class Diagnostics
{
    public static int HaneProbe(string[] args)
    {
        var countdown = 5;
        if (Arg(args, "--countdown") is { } c && (!int.TryParse(c, out countdown) || countdown < 0))
        {
            Console.WriteLine("--countdown には 0 以上の整数が必要です");
            return 2;
        }
        var withContext = args.Contains("--context");
        for (var i = countdown; i >= 1; i--)
        {
            Console.WriteLine($"countdown={i} (focus the Hane window now)");
            Thread.Sleep(1000);
        }

        // 投入先の記録と同じ経路（録音開始時の前景）で相手を決める。
        IWindowTarget? target = null;
        var captured = new Thread(() => target = new TextInjector(() => Whisparrot.Core.Settings.AppSettings.Default.Insertion).CaptureTarget() as IWindowTarget);
        captured.SetApartmentState(ApartmentState.STA);
        captured.Start();
        captured.Join();
        if (target is null)
        {
            Console.WriteLine("foreground=none (no usable top-level window)");
            return 1;
        }
        var window = new HaneWindow(target.Hwnd, target.Pid);
        Console.WriteLine($"foreground hwnd=0x{window.Hwnd:X} pid={window.Pid}");

        var client = new HaneClient();
        var sw = Stopwatch.StartNew();
        var hello = client.HelloAsync(window, CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine($"connect_auth={(hello.Ok ? "OK" : "FAILED " + hello)} elapsed_ms={sw.ElapsedMilliseconds}");
        if (!hello.Ok) return 1;
        Console.WriteLine($"version={hello.Value!.Version} features={(hello.Value.Features.Count == 0 ? "none" : string.Join(",", hello.Value.Features))}");

        if (!withContext) return 0;
        sw.Restart();
        var ctx = client.GetContextAsync(window, new HaneContextRequest(), CancellationToken.None).GetAwaiter().GetResult();
        if (!ctx.Ok)
        {
            Console.WriteLine($"context=FAILED {ctx} elapsed_ms={sw.ElapsedMilliseconds}");
            return 1;
        }
        var v = ctx.Value!;
        Console.WriteLine($"context=OK elapsed_ms={sw.ElapsedMilliseconds} before_chars={v.Before.Length} after_chars={v.After.Length} untitled={v.IsUntitled} front_matter={v.FrontMatterStatus} selection={(v.Selection.AnchorByte == v.Selection.ActiveByte ? "caret" : "range")}");
        return 0;
    }
}
