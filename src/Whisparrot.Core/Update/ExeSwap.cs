namespace Whisparrot.Core.Update;

/// <summary>
/// 入れ替えで使う 3 つのパス。実行中の exe は上書き・削除できないがリネームはできるため、
/// 現 exe を <see cref="OldPath"/> へ退避 → <see cref="NewPath"/> を本来のパスへ置く、の 2 手で足りる（補助プロセスは使わない）。
/// </summary>
public sealed record SwapPlan(string ExePath, string NewPath, string OldPath, string HealthyMarkerPath)
{
    public static SwapPlan For(string exePath) => new(exePath, exePath + ".new", exePath + ".old", exePath + ".update-ok");

    public const string RestartedArg = "--after-update";
}

public interface IFileOps
{
    bool Exists(string path);
    void Move(string from, string to);
    void Delete(string path);
}

public sealed class RealFileOps : IFileOps
{
    public bool Exists(string path) => File.Exists(path);
    public void Move(string from, string to) => File.Move(from, to);
    public void Delete(string path) => File.Delete(path);
}

public enum SwapOutcome
{
    /// <summary>新 exe が本来のパスにあり、起動も済んだ。</summary>
    Swapped,
    /// <summary>失敗したが旧 exe を元のパスへ戻した（旧版のまま起動できる）。</summary>
    RolledBack,
    /// <summary>失敗し、元のパスへ戻せなかった。<see cref="SwapResult.Detail"/> に退避先を示す。</summary>
    RollbackFailed,
}

public sealed record SwapResult(SwapOutcome Outcome, string? Detail = null);

public static class ExeSwapper
{
    /// <summary>
    /// 入れ替えて新版を起動する。<paramref name="launch"/> は入れ替え後に新 exe を起動する処理（失敗したら例外）。
    /// どの段階で失敗しても、成功した手を逆順に戻して旧 exe を元のパスへ復元する。
    /// </summary>
    public static SwapResult Apply(SwapPlan plan, Action launch, IFileOps? ops = null)
    {
        ops ??= new RealFileOps();
        try
        {
            if (!ops.Exists(plan.NewPath)) return new(SwapOutcome.RolledBack, "新しい版のファイルがありません");
            if (ops.Exists(plan.OldPath)) ops.Delete(plan.OldPath); // 前回の退避の残り。消せなければ何も変えずに中止
        }
        catch (Exception ex)
        {
            return new(SwapOutcome.RolledBack, Describe(ex));
        }

        var movedOld = false;
        var placedNew = false;
        try
        {
            ops.Move(plan.ExePath, plan.OldPath);
            movedOld = true;
            ops.Move(plan.NewPath, plan.ExePath);
            placedNew = true;
            launch();
            return new(SwapOutcome.Swapped);
        }
        catch (Exception ex)
        {
            try
            {
                if (placedNew) ops.Move(plan.ExePath, plan.NewPath);
                if (movedOld) ops.Move(plan.OldPath, plan.ExePath);
                return new(SwapOutcome.RolledBack, Describe(ex));
            }
            catch (Exception rb)
            {
                return new(SwapOutcome.RollbackFailed, $"旧版は {plan.OldPath} にあります。{plan.ExePath} へ名前を戻してください（{Describe(rb)}）");
            }
        }
    }

    private static string Describe(Exception ex) => string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : $"{ex.GetType().Name}: {ex.Message}";

    /// <summary>
    /// 起動時の片付け。再起動直後の起動（<paramref name="afterUpdate"/>）では何も消さない。
    /// 通常の起動では、前回の新版が正常に起動した印（<see cref="MarkHealthy"/>）があるときだけ旧版の退避を消す。
    /// 印が無いまま退避が残っているなら、新版が正常起動できなかった可能性があるので残す。未適用の <c>.new</c> は常に捨てる。
    /// </summary>
    public static void CleanOnStart(SwapPlan plan, bool afterUpdate, IFileOps? ops = null)
    {
        ops ??= new RealFileOps();
        if (afterUpdate) return;
        TryDelete(ops, plan.NewPath);
        if (!ops.Exists(plan.HealthyMarkerPath)) return;
        TryDelete(ops, plan.OldPath);
        TryDelete(ops, plan.HealthyMarkerPath);
    }

    /// <summary>再起動直後の起動が最後まで進んだとき（トレイ・ホットキー設定まで）に呼ぶ。次回の通常起動が旧版の退避を片付ける。</summary>
    public static void MarkHealthy(SwapPlan plan, IFileOps? ops = null)
    {
        ops ??= new RealFileOps();
        if (!ops.Exists(plan.OldPath)) return;
        try { File.WriteAllText(plan.HealthyMarkerPath, ""); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void TryDelete(IFileOps ops, string path)
    {
        try { if (ops.Exists(path)) ops.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
