using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Whisparrot.Infrastructure.Hane;

internal static class HaneNative
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

    /// <summary>接続したパイプのサーバー側プロセス ID。取得できなければ null。</summary>
    public static uint? ServerProcessId(SafePipeHandle pipe) =>
        GetNamedPipeServerProcessId(pipe, out var pid) ? pid : null;

    /// <summary>プロセスの作成時刻（FILETIME、100 ns 単位）。プロセスが無い・開けないときは null。</summary>
    public static long? ProcessCreationFileTime(uint pid)
    {
        using var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h.IsInvalid) return null;
        return GetProcessTimes(h, out var creation, out _, out _, out _) ? creation : null;
    }
}
