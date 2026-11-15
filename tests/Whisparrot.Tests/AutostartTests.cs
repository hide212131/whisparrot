using Microsoft.Win32;
using Whisparrot.Infrastructure.Settings;

namespace Whisparrot.Tests;

/// <summary>専用のテスト用サブキーで Run キーの規則を確認する（利用者の実際の自動起動には触れない）。実キーの確認は <c>--autostart-probe</c>。</summary>
public sealed class AutostartTests : IDisposable
{
    private readonly string _key = @"Software\whisparrot-test\" + Guid.NewGuid().ToString("N");

    public void Dispose() { try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\whisparrot-test", false); } catch { } }

    [Fact]
    public void On_writes_the_quoted_absolute_path_and_off_removes_only_its_value()
    {
        var exe = @"C:\Users\テスト ユーザー\My Apps\whisparrot.exe";
        var svc = new AutostartService(exe, _key);
        using (var k = Registry.CurrentUser.CreateSubKey(_key)) k.SetValue("other-app", "\"C:\\other.exe\"");

        Assert.False(svc.IsEnabled);
        svc.Apply(true);
        Assert.True(svc.IsEnabled);
        Assert.Equal("\"" + exe + "\"", svc.CurrentValue);

        svc.Apply(false);
        Assert.False(svc.IsEnabled);
        using var key = Registry.CurrentUser.OpenSubKey(_key)!;
        Assert.Equal("\"C:\\other.exe\"", key.GetValue("other-app"));
    }

    [Fact]
    public void On_repairs_a_stale_path_after_the_exe_moved_and_off_is_idempotent()
    {
        new AutostartService(@"C:\old\whisparrot.exe", _key).Apply(true);
        var moved = new AutostartService(@"D:\new\whisparrot.exe", _key);
        Assert.Equal("\"C:\\old\\whisparrot.exe\"", moved.CurrentValue);
        moved.Apply(true);
        Assert.Equal("\"D:\\new\\whisparrot.exe\"", moved.CurrentValue);
        moved.Apply(false);
        moved.Apply(false);
        Assert.False(moved.IsEnabled);
    }
}
