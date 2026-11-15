using Whisparrot.Infrastructure.Recognition;

namespace Whisparrot.Tests;

public sealed class NativeRuntimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "whisparrot-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class MemorySource(Dictionary<string, byte[]> files) : INativeResourceSource
    {
        public IReadOnlyList<string> Names => files.Keys.ToArray();
        public Stream Open(string name) => new MemoryStream(files[name]);
    }

    private static Dictionary<string, byte[]> Files() => new()
    {
        ["native/runtimes/win-x64/whisper.dll"] = [1, 2, 3, 4],
        ["native/runtimes/win-x64/ggml-base-whisper.dll"] = [5, 6],
        ["native/runtimes/vulkan/win-x64/whisper.dll"] = [7, 8, 9],
        ["native/vcrt/msvcp140.dll"] = [10, 11],
    };

    [Fact]
    public void Extracts_under_version_and_arch_then_reuses_on_second_run()
    {
        var rt = new NativeRuntime(new MemorySource(Files()), _root, "1.2.3", "win-x64");
        Assert.Equal((4, 0), rt.Extract());
        Assert.Equal(Path.Combine(_root, "1.2.3", "win-x64"), rt.Directory);
        Assert.Equal(new byte[] { 7, 8, 9 }, File.ReadAllBytes(Path.Combine(rt.Directory, "runtimes", "vulkan", "win-x64", "whisper.dll")));
        Assert.Equal((0, 4), rt.Extract());
    }

    [Fact]
    public void A_tampered_file_is_replaced_by_the_embedded_original()
    {
        var rt = new NativeRuntime(new MemorySource(Files()), _root, "1.0.0", "win-x64");
        rt.Extract();
        var victim = Path.Combine(rt.Directory, "runtimes", "win-x64", "whisper.dll");
        File.WriteAllBytes(victim, [9, 9, 9, 9]); // 同サイズで内容だけ違う
        Assert.Equal((1, 3), rt.Extract());
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(victim));
    }

    [Fact]
    public void No_temp_files_are_left_behind()
    {
        var rt = new NativeRuntime(new MemorySource(Files()), _root, "1.0.0", "win-x64");
        rt.Extract();
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp-*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Available_backends_come_from_the_embedded_names()
    {
        var rt = new NativeRuntime(new MemorySource(Files()), _root, "1.0.0", "win-x64");
        Assert.Equal(["cpu", "vulkan"], rt.AvailableBackends.OrderBy(x => x));
        Assert.Equal("runtimes/vulkan/win-x64/whisper.dll", rt.WhisperDllRelative("vulkan"));
        Assert.Equal("runtimes/win-x64/whisper.dll", rt.WhisperDllRelative("cpu"));

        var cpuOnly = Files();
        cpuOnly.Remove("native/runtimes/vulkan/win-x64/whisper.dll");
        var rt2 = new NativeRuntime(new MemorySource(cpuOnly), _root, "1.0.0", "win-x64");
        Assert.Equal(["cpu"], rt2.AvailableBackends);
        Assert.Null(rt2.WhisperDllRelative("vulkan"));
    }
}
