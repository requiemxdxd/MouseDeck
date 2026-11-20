using System.Runtime.InteropServices;
using MouseDeck.Core.Models;
using MouseDeck.Win32.Windowing;

namespace MouseDeck.Win32.Tray;

/// <summary>
/// Lightweight native Shell_NotifyIconW wrapper for Windows 11 system tray.
/// Strictly registers a single tray icon (ID 1001), supports NIS_HIDDEN when the Taskbar Pill widget
/// is active (avoiding duplicate indicators while preserving native toast notifications),
/// and debounces Left-Click and Right-Click events.
/// </summary>
public sealed class NativeNotifyIcon : IDisposable
{
    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_STATE = 0x00000008;
    private const uint NIF_INFO = 0x00000010;

    private const uint NIS_HIDDEN = 0x00000001;

    private const uint NIIF_INFO = 0x00000001;
    private const uint NIIF_WARNING = 0x00000002;
    private const uint NIIF_RESPECT_QUIET_TIME = 0x00000080;

    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int NIN_SELECT = 0x0400;
    private const int NIN_KEYSELECT = 0x0401;
    private const int NIN_BALLOONUSERCLICK = 0x0405;

    private const uint PrimaryIconId = 1001;

    private readonly MessageWindow _messageWindow;
    private IntPtr _currentHIcon;
    private string _currentTooltip = "MouseDeck";
    private bool _isAdded;
    private bool _isHidden;
    private long _lastLeftClickTick;
    private long _lastRightClickTick;
    private bool _disposed;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NOTIFYICONIDENTIFIER
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out FlyoutPositioner.RECT iconLocation);

    public NativeNotifyIcon(MessageWindow messageWindow)
    {
        _messageWindow = messageWindow;
        _messageWindow.TrayMessageReceived += OnTrayMessage;
        _messageWindow.TaskbarRecreated += OnTaskbarRecreated;
    }

    public event Action? LeftClicked;
    public event Action? RightClicked;
    public event Action? NotificationClicked;

    public void Initialize(MouseRuntimeState initialState, TrayIconStyle style, bool hidden = false)
    {
        _isHidden = hidden;
        Update(initialState, style, hidden);
    }

    public void Update(MouseRuntimeState state, TrayIconStyle style, bool hidden = false)
    {
        if (_disposed) return;

        _isHidden = hidden;
        var newIcon = TrayIconRenderer.RenderIcon(state, style);
        var oldIcon = _currentHIcon;
        _currentHIcon = newIcon;
        _currentTooltip = FormatTooltip(state);

        if (!_isAdded)
        {
            var data = CreateBaseData(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_STATE);
            data.uCallbackMessage = MessageWindow.WM_APP_TRAYICON;
            data.hIcon = _currentHIcon;
            data.szTip = _currentTooltip;
            data.dwState = _isHidden ? NIS_HIDDEN : 0;
            data.dwStateMask = NIS_HIDDEN;
            _isAdded = Shell_NotifyIconW(NIM_ADD, ref data);
        }
        else
        {
            var data = CreateBaseData(NIF_ICON | NIF_TIP | NIF_STATE);
            data.hIcon = _currentHIcon;
            data.szTip = _currentTooltip;
            data.dwState = _isHidden ? NIS_HIDDEN : 0;
            data.dwStateMask = NIS_HIDDEN;
            Shell_NotifyIconW(NIM_MODIFY, ref data);
        }

        if (oldIcon != IntPtr.Zero)
        {
            TrayIconRenderer.DestroyIcon(oldIcon);
        }
    }

    public void ShowNotification(string title, string message, bool isWarning = true)
    {
        if (_disposed || !_isAdded) return;

        var data = CreateBaseData(NIF_INFO);
        data.szInfoTitle = Truncate(title, 63);
        data.szInfo = Truncate(message, 255);
        data.dwInfoFlags = (isWarning ? NIIF_WARNING : NIIF_INFO) | NIIF_RESPECT_QUIET_TIME;
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    public FlyoutPositioner.RECT? GetIconRect()
    {
        if (_isHidden) return null;

        var id = new NOTIFYICONIDENTIFIER
        {
            cbSize = Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
            hWnd = _messageWindow.Handle,
            uID = PrimaryIconId,
            guidItem = Guid.Empty
        };

        return Shell_NotifyIconGetRect(ref id, out var rect) == 0 ? rect : null;
    }

    private void OnTaskbarRecreated()
    {
        _isAdded = false;
        if (_currentHIcon != IntPtr.Zero)
        {
            var data = CreateBaseData(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_STATE);
            data.uCallbackMessage = MessageWindow.WM_APP_TRAYICON;
            data.hIcon = _currentHIcon;
            data.szTip = _currentTooltip;
            data.dwState = _isHidden ? NIS_HIDDEN : 0;
            data.dwStateMask = NIS_HIDDEN;
            _isAdded = Shell_NotifyIconW(NIM_ADD, ref data);
        }
    }

    private void OnTrayMessage(int lParam, IntPtr wParam)
    {
        int evt = lParam & 0xFFFF;
        long now = Environment.TickCount64;

        switch (evt)
        {
            case WM_LBUTTONUP:
            case NIN_SELECT:
            case NIN_KEYSELECT:
                if (now - _lastLeftClickTick < 110) return;
                _lastLeftClickTick = now;
                LeftClicked?.Invoke();
                break;

            case WM_RBUTTONUP:
            case WM_CONTEXTMENU:
                if (now - _lastRightClickTick < 110) return;
                _lastRightClickTick = now;
                RightClicked?.Invoke();
                break;

            case NIN_BALLOONUSERCLICK:
                NotificationClicked?.Invoke();
                break;
        }
    }

    private NOTIFYICONDATAW CreateBaseData(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _messageWindow.Handle,
        uID = PrimaryIconId,
        uFlags = flags,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty
    };

    public static string FormatTooltip(MouseRuntimeState state)
    {
        string statusText = state.Status switch
        {
            MouseConnectionStatus.Charging => "Charging",
            MouseConnectionStatus.Active => "Active",
            MouseConnectionStatus.Sleeping => "Sleep",
            MouseConnectionStatus.Connecting => "Connecting",
            _ => "Disconnected"
        };

        string pctText = state.BatteryPercent.HasValue ? $"{state.BatteryPercent.Value}%" : "—";
        string voltText = state.VoltageMv is int mv and > 2500 ? $" ({mv / 1000.0:0.00} V)" : string.Empty;
        string line1 = Truncate(state.DeviceName, 40);
        string line2 = $"Battery: {pctText}{voltText} • {statusText}";
        string line3 = $"DPI: {state.CurrentDpi} | {state.CurrentPollingRateHz} Hz | {Truncate(state.ActiveProfileName, 18)}";

        return Truncate($"{line1}\n{line2}\n{line3}", 127);
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _messageWindow.TrayMessageReceived -= OnTrayMessage;
        _messageWindow.TaskbarRecreated -= OnTaskbarRecreated;

        if (_isAdded)
        {
            var data = CreateBaseData(0);
            Shell_NotifyIconW(NIM_DELETE, ref data);
            _isAdded = false;
        }

        if (_currentHIcon != IntPtr.Zero)
        {
            TrayIconRenderer.DestroyIcon(_currentHIcon);
            _currentHIcon = IntPtr.Zero;
        }
    }
}
