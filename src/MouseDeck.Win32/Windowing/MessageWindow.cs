using System.Runtime.InteropServices;
using System.Windows.Interop;
using MouseDeck.Win32.Hid;

namespace MouseDeck.Win32.Windowing;

/// <summary>
/// Hidden top-level message window handling Win32 notifications with 0% idle CPU:
/// WM_DEVICECHANGE (PnP USB/HID arrival &amp; removal), WM_HOTKEY, tray icon callbacks,
/// Explorer TaskbarCreated broadcasts, and single-instance wakeup messages.
/// </summary>
public sealed class MessageWindow : IDisposable
{
    public const int WM_APP_TRAYICON = 0x8001;
    private const int WM_HOTKEY = 0x0312;
    private const int WM_DEVICECHANGE = 0x0219;
    private const int WM_DISPLAYCHANGE = 0x007E;
    private const int WM_SETTINGCHANGE = 0x001A;
    private const int WM_QUERYENDSESSION = 0x0011;
    private const int WM_ENDSESSION = 0x0016;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    private const int DBT_DEVTYP_DEVICEINTERFACE = 0x00000005;
    private const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;

    private readonly HwndSource _hwndSource;
    private readonly IntPtr _deviceNotifyHandle;
    private readonly uint _taskbarCreatedMsg;
    private readonly uint _showInstanceMsg;
    private bool _disposed;

    [StructLayout(LayoutKind.Sequential)]
    private struct DEV_BROADCAST_DEVICEINTERFACE
    {
        public int dbcc_size;
        public int dbcc_devicetype;
        public int dbcc_reserved;
        public Guid dbcc_classguid;
        public short dbcc_name;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr RegisterDeviceNotificationW(IntPtr hRecipient, IntPtr notificationFilter, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterDeviceNotification(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    public MessageWindow()
    {
        var parameters = new HwndSourceParameters("MouseDeck_MessageHost")
        {
            Width = 0,
            Height = 0,
            PositionX = -32000,
            PositionY = -32000,
            WindowStyle = 0,
            ExtendedWindowStyle = 0x00000080 // WS_EX_TOOLWINDOW
        };

        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProc);

        _taskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");
        _showInstanceMsg = RegisterWindowMessageW("MouseDeck_SingleInstance_Wakeup");

        _deviceNotifyHandle = RegisterHidPnPNotifications(_hwndSource.Handle);
    }

    public IntPtr Handle => _hwndSource.Handle;

    public event Action<int, IntPtr>? TrayMessageReceived;
    public event Action<int>? HotkeyPressed;
    public event Action<bool>? HidDeviceTopologyChanged; // true = arrival, false = removal
    public event Action? TaskbarRecreated;
    public event Action? SingleInstanceWakeupRequested;
    public event Action? DisplayOrSettingsChanged;
    public event Action? SessionEnding;

    public static void BroadcastWakeupToExistingInstance()
    {
        uint msg = RegisterWindowMessageW("MouseDeck_SingleInstance_Wakeup");
        if (msg != 0)
        {
            PostMessageW(HWND_BROADCAST, msg, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private static IntPtr RegisterHidPnPNotifications(IntPtr hwnd)
    {
        NativeHidApi.HidD_GetHidGuid(out var hidGuid);
        var filter = new DEV_BROADCAST_DEVICEINTERFACE
        {
            dbcc_size = Marshal.SizeOf<DEV_BROADCAST_DEVICEINTERFACE>(),
            dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE,
            dbcc_classguid = hidGuid
        };

        IntPtr mem = Marshal.AllocHGlobal(filter.dbcc_size);
        try
        {
            Marshal.StructureToPtr(filter, mem, false);
            return RegisterDeviceNotificationW(hwnd, mem, DEVICE_NOTIFY_WINDOW_HANDLE);
        }
        finally
        {
            Marshal.FreeHGlobal(mem);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_APP_TRAYICON)
        {
            int lowWord = unchecked((int)lParam.ToInt64()) & 0xFFFF;
            TrayMessageReceived?.Invoke(lowWord, wParam);
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == WM_HOTKEY)
        {
            HotkeyPressed?.Invoke(wParam.ToInt32());
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == WM_DEVICECHANGE)
        {
            int evt = wParam.ToInt32();
            if (evt == DBT_DEVICEARRIVAL)
                HidDeviceTopologyChanged?.Invoke(true);
            else if (evt == DBT_DEVICEREMOVECOMPLETE)
                HidDeviceTopologyChanged?.Invoke(false);
        }
        else if (_taskbarCreatedMsg != 0 && msg == (int)_taskbarCreatedMsg)
        {
            TaskbarRecreated?.Invoke();
        }
        else if (_showInstanceMsg != 0 && msg == (int)_showInstanceMsg)
        {
            SingleInstanceWakeupRequested?.Invoke();
        }
        else if (msg == WM_DISPLAYCHANGE || msg == WM_SETTINGCHANGE)
        {
            DisplayOrSettingsChanged?.Invoke();
        }
        else if (msg == WM_ENDSESSION && wParam != IntPtr.Zero)
        {
            SessionEnding?.Invoke();
        }
        else if (msg == WM_QUERYENDSESSION)
        {
            handled = true;
            return new IntPtr(1);
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_deviceNotifyHandle != IntPtr.Zero)
        {
            UnregisterDeviceNotification(_deviceNotifyHandle);
        }

        _hwndSource.RemoveHook(WndProc);
        _hwndSource.Dispose();
    }
}
