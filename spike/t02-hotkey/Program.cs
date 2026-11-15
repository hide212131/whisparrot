using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Spike.T02;

/// <summary>
/// RegisterHotKey の最小 probe（設計 §5 / T02）。
///   hotkey-probe register        : 3 組合せの登録可否と GetLastError を出して解除する
///   hotkey-probe listen [秒]     : 3 組合せを登録し、WM_HOTKEY の到着をタイムスタンプ付きで記録する（既定 60 秒）
/// 登録の成功は「キーが配送される」ことを意味しない。配送は対話セッションで listen を使って確認する。
/// </summary>
internal static class Program
{
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
    private const uint VK_J = 0x4A;
    private const uint WM_HOTKEY = 0x0312;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private static readonly (int Id, string Name, uint Mod)[] Combos =
    [
        (1, "Win+J", MOD_WIN),
        (2, "Ctrl+Win+J", MOD_CONTROL | MOD_WIN),
        (3, "Ctrl+Alt+J", MOD_CONTROL | MOD_ALT),
    ];

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public UIntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool PeekMessageW(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);

    [DllImport("kernel32.dll")]
    private static extern bool ProcessIdToSessionId(uint pid, out uint sessionId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetProcessWindowStation();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformationW(IntPtr h, int index, StringBuilder buf, int len, out int needed);

    private static int Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "register";
        PrintEnvironment();

        var hwnd = CreateWindowExW(0, "STATIC", "hotkey-probe", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            Console.WriteLine($"create_message_window=FAILED win32_error={Marshal.GetLastWin32Error()}");
            return 1;
        }
        Console.WriteLine("create_message_window=ok");

        var registered = new List<(int Id, string Name)>();
        foreach (var (id, name, mod) in Combos)
        {
            var ok = RegisterHotKey(hwnd, id, mod | MOD_NOREPEAT, VK_J);
            var err = ok ? 0 : Marshal.GetLastWin32Error();
            var msg = ok ? "" : new Win32Exception(err).Message;
            Console.WriteLine($"register[{name}]={(ok ? "OK" : "FAILED")} win32_error={err} {msg}".TrimEnd());
            if (ok) registered.Add((id, name));
        }

        if (mode == "listen")
        {
            var seconds = args.Length > 1 ? int.Parse(args[1]) : 60;
            Console.WriteLine($"listening_seconds={seconds} (押下する組合せ: Win+J / Ctrl+Win+J / Ctrl+Alt+J。登録成功したものだけ届く)");
            Console.Out.Flush();
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            var count = 0;
            while (DateTime.UtcNow < deadline)
            {
                if (PeekMessageW(out var m, IntPtr.Zero, 0, 0, 1))
                {
                    if (m.message == WM_HOTKEY)
                    {
                        count++;
                        var name = Combos.First(c => c.Id == (int)m.wParam).Name;
                        Console.WriteLine($"WM_HOTKEY {DateTime.Now:HH:mm:ss.fff} id={(int)m.wParam} combo={name} n={count}");
                        Console.Out.Flush();
                    }
                }
                else
                {
                    Thread.Sleep(10);
                }
            }
            Console.WriteLine($"wm_hotkey_total={count}");
        }

        foreach (var (id, name) in registered)
        {
            var ok = UnregisterHotKey(hwnd, id);
            Console.WriteLine($"unregister[{name}]={(ok ? "OK" : "FAILED")}");
        }
        DestroyWindow(hwnd);
        Console.WriteLine("delivery=UNVERIFIED (登録成功はキー配送を保証しない。対話セッションで listen を使って確認すること)");
        return 0;
    }

    private static void PrintEnvironment()
    {
        ProcessIdToSessionId((uint)Environment.ProcessId, out var session);
        var sb = new StringBuilder(256);
        string station = "?";
        if (GetUserObjectInformationW(GetProcessWindowStation(), 2 /* UOI_NAME */, sb, sb.Capacity * 2, out _))
        {
            station = sb.ToString();
        }
        Console.WriteLine($"os={RuntimeInformation.OSDescription}");
        Console.WriteLine($"process_arch={RuntimeInformation.ProcessArchitecture} os_arch={RuntimeInformation.OSArchitecture}");
        Console.WriteLine($"session_id={session} window_station={station} interactive={Environment.UserInteractive}");
    }
}
