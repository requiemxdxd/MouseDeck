using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MouseDeck.Win32.Automation;

/// <summary>
/// Event-driven active process watcher using Win32 SetWinEventHook(EVENT_SYSTEM_FOREGROUND).
/// Consumes 0% CPU in idle (zero polling timers).
/// </summary>
public sealed class ProcessWatcherService : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    // Keep strong reference to prevent GC collecting delegate passed to native Win32 hook
    private readonly WinEventDelegate _hookProc;
    private IntPtr _hookHandle;
    private bool _disposed;

    public ProcessWatcherService()
    {
        _hookProc = OnWinEvent;
    }

    public event Action<string?>? ForegroundProcessChanged;
    public event Action<string?>? InitialProcessObserved;

    public void Start()
    {
        if (_hookHandle != IntPtr.Zero) return;

        _hookHandle = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND,
            EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _hookProc,
            0,
            0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);

        // Read initial foreground process purely for display — never trigger a profile write on startup
        var initialHwnd = GetForegroundWindow();
        if (initialHwnd != IntPtr.Zero)
        {
            InitialProcessObserved?.Invoke(ResolveExeNameFromWindow(initialHwnd));
        }
    }

    private void OnWinEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (hwnd == IntPtr.Zero) return;
        var exe = ResolveExeNameFromWindow(hwnd);
        ForegroundProcessChanged?.Invoke(exe);
    }

    public static string? ResolveExeNameFromWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        _ = GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return null;

        IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProcess == IntPtr.Zero)
            return null;

        try
        {
            var sb = new StringBuilder(512);
            uint size = (uint)sb.Capacity;
            if (!QueryFullProcessImageNameW(hProcess, 0, sb, ref size))
                return null;

            return Path.GetFileName(sb.ToString()).ToLowerInvariant();
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    /// <summary>
    /// Lists visible top-level application executables so users can quickly pick a running game/app for rules.
    /// </summary>
    public static IReadOnlyList<string> GetRunningWindowExecutables()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        uint ownPid = (uint)Environment.ProcessId;

        EnumWindows((hwnd, lParam) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindowTextLengthW(hwnd) <= 0)
                return true;

            _ = GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0 || pid == ownPid)
                return true;

            var exe = ResolveExeNameFromWindow(hwnd);
            if (!string.IsNullOrWhiteSpace(exe) &&
                !exe.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) &&
                !exe.Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase) &&
                !exe.Equals("TextInputHost.exe", StringComparison.OrdinalIgnoreCase))
            {
                names.Add(exe);
            }
            return true;
        }, IntPtr.Zero);

        return names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWinEvent(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }
}
