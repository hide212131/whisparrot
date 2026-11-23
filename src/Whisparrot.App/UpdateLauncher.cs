using System.Diagnostics;
using System.IO;
using Whisparrot.Core.Update;
using Whisparrot.Infrastructure;

namespace Whisparrot.App;

/// <summary>更新後の新版の起動。入れ替えの前に <see cref="Preload"/> を呼ぶこと（理由は同メソッド）。</summary>
internal static class UpdateLauncher
{
    /// <summary>
    /// 入れ替えで実行中の exe をリネームした後は、単一ファイルの exe から未読込のアセンブリ（System.Diagnostics.Process など）を
    /// 新たに読み込めない（FileNotFoundException）。起動に必要な型を入れ替えの前に読み込み・JIT 済みにしておく。
    /// </summary>
    public static void Preload()
    {
        using var self = Process.GetCurrentProcess();
        var psi = CreateStartInfo(SwapPlan.For(Environment.ProcessPath!));
        _ = psi.ArgumentList.Count;
        _ = new System.ComponentModel.Win32Exception(0).Message; // 起動失敗の例外型（System.ComponentModel.Primitives）も先に読む
        _ = new FileInfo(psi.FileName).Exists;
        System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(typeof(UpdateLauncher).GetMethod(nameof(Start))!.MethodHandle);
    }

    private static ProcessStartInfo CreateStartInfo(SwapPlan plan)
    {
        var psi = new ProcessStartInfo(plan.ExePath) { UseShellExecute = false, WorkingDirectory = AppPaths.AppDirectory };
        psi.ArgumentList.Add(SwapPlan.RestartedArg);
        return psi;
    }

    /// <summary>入れ替え後の exe を <c>--after-update</c> 付きで起動する。起動できなければ例外。</summary>
    public static void Start(SwapPlan plan)
    {
        using var p = Process.Start(CreateStartInfo(plan)) ?? throw new InvalidOperationException("新しい版を起動できませんでした");
    }
}
