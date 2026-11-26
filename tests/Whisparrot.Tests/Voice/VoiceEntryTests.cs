using Whisparrot.Infrastructure.Settings;

namespace Whisparrot.Tests.Voice;

/// <summary>エンジンモードの起動入口が、常駐アプリの資源（mutex・ホットキー・トレイ・投入）に触れないことの確認。</summary>
public sealed class VoiceEntryTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Whisparrot.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    [Fact]
    public void Engine_entry_is_dispatched_before_the_singleton_mutex_and_the_tray()
    {
        var program = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Whisparrot.App", "Program.cs"));
        var engine = program.IndexOf("VoiceEngineCommand.Flag", StringComparison.Ordinal);
        Assert.True(engine >= 0);
        Assert.True(engine < program.IndexOf("new Mutex(", StringComparison.Ordinal));
        Assert.True(engine < program.IndexOf("TrayApp.Run(", StringComparison.Ordinal));
    }

    [Fact]
    public void Engine_entry_does_not_reference_tray_hotkey_injection_or_update_types()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Whisparrot.App", "VoiceEngineCommand.cs"));
        foreach (var forbidden in new[] { "TrayApp", "HotkeyService", "TextInjector", "PillWindow", "NotifyIcon", "Mutex", "AutostartService", "UpdateService", "ExeSwapper", "SettingsWindow", "Clipboard" })
            Assert.DoesNotContain(forbidden, src.Replace("NoInjector", "").Replace("ITextInjector", ""));
    }

    [Fact]
    public void Read_only_settings_do_not_touch_files_beside_the_config()
    {
        var dir = Directory.CreateTempSubdirectory("wp-ro-").FullName;
        try
        {
            var config = Path.Combine(dir, "config.json");
            var stale = config + ".0a1b2c3d.tmp";
            File.WriteAllText(stale, "x");
            var result = new JsonSettingsStore(config, readOnly: true).Load();
            Assert.True(File.Exists(stale)); // 読み取り専用では掃除しない
            Assert.False(File.Exists(config));
            Assert.NotNull(result.Settings);
            new JsonSettingsStore(config).Load();
            Assert.False(File.Exists(stale)); // 通常の読み込みは従来どおり掃除する
        }
        finally { Directory.Delete(dir, true); }
    }
}
