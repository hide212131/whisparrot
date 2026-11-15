using System.Diagnostics;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Spike.S01;

/// <summary>
/// --recognize &lt;model.bin&gt; &lt;audio.wav&gt; [--lang en|ja] [--order cpu|vulkan|cuda|cuda12|auto] [--repeat N]
/// ランタイム選択順を RuntimeLibraryOrder で固定して認識し、実際にロードされたライブラリを出力する。
/// </summary>
internal static class Recognize
{
    public static int Run(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: --recognize <model> <wav> [--lang en] [--order cpu|vulkan|cuda|cuda12|auto] [--repeat N]");
            return 2;
        }
        var model = args[1];
        var wav = args[2];
        var lang = Opt(args, "--lang") ?? "en";
        var order = Opt(args, "--order") ?? "auto";
        var repeat = int.Parse(Opt(args, "--repeat") ?? "1");

        RuntimeOptions.RuntimeLibraryOrder = order switch
        {
            "cpu" => [RuntimeLibrary.Cpu],
            "vulkan" => [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu],
            "cuda" => [RuntimeLibrary.Cuda, RuntimeLibrary.Cpu],
            "cuda12" => [RuntimeLibrary.Cuda12, RuntimeLibrary.Cpu],
            "auto" => [RuntimeLibrary.Cuda, RuntimeLibrary.Cuda12, RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu],
            _ => throw new ArgumentException($"unknown order {order}"),
        };

        try
        {
            var swLoad = Stopwatch.StartNew();
            using var factory = WhisperFactory.FromPath(model);
            swLoad.Stop();
            Console.WriteLine($"loaded_library={RuntimeOptions.LoadedLibrary}");
            Console.WriteLine($"model_load_ms={swLoad.ElapsedMilliseconds}");

            using var processor = factory.CreateBuilder()
                .WithLanguage(lang)
                .WithThreads(Math.Max(1, Math.Min(8, Environment.ProcessorCount - 1)))
                .Build();

            for (var i = 0; i < repeat; i++)
            {
                var sw = Stopwatch.StartNew();
                var text = new System.Text.StringBuilder();
                using var fs = File.OpenRead(wav);
                foreach (var seg in processor.Process(fs))
                {
                    text.Append(seg.Text);
                }
                sw.Stop();
                Console.WriteLine($"run{i + 1}_recognize_ms={sw.ElapsedMilliseconds}");
                if (i == 0) Console.WriteLine($"text={text.ToString().Trim()}");
            }
            var p = Process.GetCurrentProcess();
            Console.WriteLine($"peak_working_set_mib={p.PeakWorkingSet64 / 1048576.0:F1}");
            Console.WriteLine($"process_arch={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"error={ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"loaded_library={RuntimeOptions.LoadedLibrary}");
            return 1;
        }
    }

    private static string? Opt(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
