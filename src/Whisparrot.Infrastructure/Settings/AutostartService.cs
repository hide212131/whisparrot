using Microsoft.Win32;

namespace Whisparrot.Infrastructure.Settings;

/// <summary>
/// 設計 §3.1: <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run\whisparrot</c> に引用符付きの絶対 exe パスを書く。
/// 管理者権限は要らない。OFF では自分の値だけを削除する。キーのパスはテスト用に差し替えられる。
/// </summary>
public sealed class AutostartService(string exePath, string runKeyPath = AutostartService.DefaultRunKey, string valueName = "whisparrot")
{
    public const string DefaultRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string ExpectedValue => "\"" + exePath + "\"";

    public string? CurrentValue
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(runKeyPath, writable: false);
            return key?.GetValue(valueName) as string;
        }
    }

    public bool IsEnabled => CurrentValue is not null;

    /// <summary>ON/OFF を反映する。ON のとき値が現在の exe と違えば修復（書き直し）する。失敗は例外で返す（保存成功と見せない）。</summary>
    public void Apply(bool enabled)
    {
        if (enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(runKeyPath, writable: true)
                ?? throw new IOException("Run キーを開けません");
            if (!string.Equals(key.GetValue(valueName) as string, ExpectedValue, StringComparison.Ordinal))
                key.SetValue(valueName, ExpectedValue, RegistryValueKind.String);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(runKeyPath, writable: true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }
}
