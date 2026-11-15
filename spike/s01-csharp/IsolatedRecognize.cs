using System.Diagnostics;

namespace Spike.S01;

/// <summary>
/// --recognize-isolated &lt;model&gt; &lt;wav&gt; [--lang ..] [--order vulkan|cuda|auto]
/// 設計 §3.2 の最小再現: 認識を同じ exe の子プロセスで実行し、GPU 側の子が異常終了しても
/// 親は生き残って同じ入力を CPU の子で一度だけ再試行する。
/// </summary>
internal static class IsolatedRecognize
{
    public static int Run(string[] args)
    {
        var order = Opt(args, "--order") ?? "auto";
        var first = RunChild(args, order);
        Console.WriteLine($"attempt1 order={order} exit={first.ExitCode} ok={first.Ok}");
        if (first.Ok)
        {
            Console.Write(first.Output);
            return 0;
        }

        Console.WriteLine("attempt1 failed; parent still alive, retrying once with cpu");
        var second = RunChild(args, "cpu");
        Console.WriteLine($"attempt2 order=cpu exit={second.ExitCode} ok={second.Ok}");
        Console.Write(second.Output);
        return second.Ok ? 0 : 1;
    }

    private static (int ExitCode, bool Ok, string Output) RunChild(string[] args, string order)
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!)
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--recognize");
        psi.ArgumentList.Add(args[1]);
        psi.ArgumentList.Add(args[2]);
        psi.ArgumentList.Add("--lang");
        psi.ArgumentList.Add(Opt(args, "--lang") ?? "en");
        psi.ArgumentList.Add("--order");
        psi.ArgumentList.Add(order);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, p.ExitCode == 0 && output.Contains("text="), output);
    }

    private static string? Opt(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
