using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using MouseDeck.Core.Models;
using MouseDeck.Win32.Windowing;

namespace MouseDeck.Win32.Tray;

/// <summary>
/// Native Windows 11 Taskbar Pill widget ([🖱 30%] or [MAD 8K 30%]) positioned cleanly over the taskbar
/// immediately to the left of TrayNotifyWnd (the ^ system tray chevron).
/// Uses a standalone DirectWrite per-pixel alpha layered window (WS_POPUP | WS_EX_LAYERED | WS_EX_TOPMOST)
/// with zero explorer.exe injection, instant WM_LBUTTONDOWN response, automatic fullscreen game/video hiding, and 0% idle CPU.
/// </summary>
public sealed class TaskbarPillWidget : IDisposable
{
    private const string WindowClassName = "MouseDeck_TaskbarPillWnd_v3";

    private const uint CS_DBLCLKS = 0x0008;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_TOPMOST = 0x00000008;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint GA_ROOT = 2;

    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_MOUSELEAVE = 0x02A3;
    private const int WM_SETCURSOR = 0x0020;

    private const uint TME_LEAVE = 0x00000002;
    private const uint ULW_ALPHA = 0x00000002;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;
    private static readonly IntPtr IDC_HAND = new(32649);

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT
    {
        public uint cbSize;
        public uint dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public FlyoutPositioner.RECT rcMonitor;
        public FlyoutPositioner.RECT rcWork;
        public uint dwFlags;
    }

    private enum QUERY_USER_NOTIFICATION_STATE
    {
        QUNS_NOT_PRESENT = 1,
        QUNS_BUSY = 2,
        QUNS_RUNNING_D3D_FULL_SCREEN = 3,
        QUNS_PRESENTATION_MODE = 4,
        QUNS_ACCEPTS_NOTIFICATIONS = 5,
        QUNS_QUIET_TIME = 6,
        QUNS_APP = 7
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowExW(IntPtr hwndParent, IntPtr hwndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(FlyoutPositioner.POINT Point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out FlyoutPositioner.POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out FlyoutPositioner.RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(
        IntPtr hwnd,
        IntPtr hdcDst,
        ref FlyoutPositioner.POINT pptDst,
        ref SIZE psize,
        IntPtr hdcSrc,
        ref FlyoutPositioner.POINT pptSrc,
        uint crKey,
        ref BLENDFUNCTION pblend,
        uint dwFlags);

    private const uint ABM_GETSTATE = 0x00000004;
    private const uint ABS_AUTOHIDE = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public FlyoutPositioner.RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out QUERY_USER_NOTIFICATION_STATE pquns);

    [DllImport("shell32.dll")]
    private static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    private readonly WndProcDelegate _wndProcDelegate;
    private readonly DispatcherTimer _layoutWatchTimer;
    private IntPtr _hwnd;
    private MouseRuntimeState _lastState = new();
    private TrayIconStyle _currentStyle = TrayIconStyle.TaskbarPillIconAndPercent;
    private bool _enabled;
    private bool _isHovered;
    private bool _isPressed;
    private bool _isTrackingLeave;
    private bool _isHiddenForFullscreen;
    private int _lastTrayNotifyLeft;
    private int _lastTaskbarTop;
    private int _lastTaskbarHeight;
    private double _lastDpiScale = 1.0;
    private long _lastClickTick;
    private long _lastRightClickTick;
    private bool _lastReportedAttached;
    private bool _disposed;

    public TaskbarPillWidget()
    {
        _wndProcDelegate = WndProc;
        // 200ms lightweight Z-order, fullscreen & geometry sync keeps fullscreen transitions snappy at 0% CPU
        _layoutWatchTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _layoutWatchTimer.Tick += (_, _) => SyncTaskbarPlacement();
    }

    public event Action? LeftClicked;
    public event Action? RightClicked;
    public event Action<bool>? AttachmentStateChanged;

    public IntPtr Handle => _hwnd;

    public bool IsAttachedAndVisible =>
        !_disposed && _enabled && _hwnd != IntPtr.Zero && IsWindow(_hwnd) && !_isHiddenForFullscreen && IsWindowVisible(_hwnd);

    /// <summary>
    /// True whenever Taskbar Pill mode is active and its HWND exists — even while temporarily
    /// hidden for a fullscreen app or auto-hide taskbar — so the fallback 16x16 tray icon
    /// never flashes or shifts TrayNotifyWnd when entering/exiting fullscreen.
    /// </summary>
    public bool IsPillModeActive =>
        !_disposed && _enabled && _hwnd != IntPtr.Zero && IsWindow(_hwnd);

    public static bool IsPillStyle(TrayIconStyle style) =>
        style is TrayIconStyle.TaskbarPillIconAndPercent or TrayIconStyle.TaskbarPillNameAndPercent;

    public bool IsCursorOverPill()
    {
        if (!IsAttachedAndVisible) return false;
        if (!GetWindowRect(_hwnd, out var r) || !GetCursorPos(out var pt))
            return false;

        return pt.X >= r.Left && pt.X <= r.Right && pt.Y >= r.Top && pt.Y <= r.Bottom;
    }

    /// <summary>
    /// Immediately syncs visibility (hiding for fullscreen apps) and re-asserts topmost Z-order
    /// above Shell_TrayWnd when the user clicks the taskbar or switches foreground windows.
    /// </summary>
    public void EnsureTopmostOrder()
    {
        if (_disposed || !_enabled) return;
        SyncTaskbarPlacement();
    }

    public bool Update(MouseRuntimeState state, TrayIconStyle style)
    {
        if (_disposed) return false;
        _lastState = state;
        _currentStyle = style;

        if (!IsPillStyle(style))
        {
            HideAndDetach();
            NotifyAttachmentChangedIfNeeded();
            return false;
        }

        _enabled = true;
        if (!_layoutWatchTimer.IsEnabled)
            _layoutWatchTimer.Start();

        if (!TryGetTaskbarGeometry(out _, out _, out _))
        {
            _isHiddenForFullscreen = true;
            if (_hwnd != IntPtr.Zero && IsWindow(_hwnd))
                ShowWindow(_hwnd, SW_HIDE);
            NotifyAttachmentChangedIfNeeded();
            return IsPillModeActive;
        }

        if (!EnsureWindowCreated())
        {
            NotifyAttachmentChangedIfNeeded();
            return false;
        }

        _isHiddenForFullscreen = IsFullscreenBlockingTaskbar();
        RenderAndPosition();
        NotifyAttachmentChangedIfNeeded();
        return IsPillModeActive;
    }

    public void OnTaskbarOrDisplayChanged()
    {
        if (_disposed || !_enabled) return;
        SyncTaskbarPlacement(forceRedraw: true);
    }

    public FlyoutPositioner.RECT? GetScreenRect()
    {
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd) || !IsWindowVisible(_hwnd))
            return null;

        return GetWindowRect(_hwnd, out var rect) ? rect : null;
    }

    private void HideAndDetach()
    {
        _enabled = false;
        _layoutWatchTimer.Stop();
        if (_hwnd != IntPtr.Zero && IsWindow(_hwnd))
        {
            ShowWindow(_hwnd, SW_HIDE);
        }
    }

    private bool EnsureWindowCreated()
    {
        if (_hwnd != IntPtr.Zero && IsWindow(_hwnd))
            return true;

        EnsureWindowClassRegistered();
        IntPtr hInst = GetModuleHandleW(null);
        // Note: Do NOT set WS_EX_NOACTIVATE so clicking the pill natively grants MouseDeck.exe
        // Win32 SetForegroundWindow rights to activate and focus FlyoutWindow reliably every time.
        _hwnd = CreateWindowExW(
            WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST,
            WindowClassName,
            "MouseDeckTaskbarPill",
            WS_POPUP,
            0, 0, 68, 40,
            IntPtr.Zero,
            IntPtr.Zero,
            hInst,
            IntPtr.Zero);

        return _hwnd != IntPtr.Zero;
    }

    private static bool IsTaskbarAutoHideEnabled()
    {
        try
        {
            var abd = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
            uint state = (uint)SHAppBarMessage(ABM_GETSTATE, ref abd);
            return (state & ABS_AUTOHIDE) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetTaskbarGeometry(out IntPtr taskbarHwnd, out FlyoutPositioner.RECT tbRect, out FlyoutPositioner.RECT tnRect)
    {
        taskbarHwnd = FindWindowW("Shell_TrayWnd", null);
        tbRect = default;
        tnRect = default;

        if (taskbarHwnd == IntPtr.Zero || !IsWindowVisible(taskbarHwnd))
            return false;

        IntPtr trayNotify = FindWindowExW(taskbarHwnd, IntPtr.Zero, "TrayNotifyWnd", null);
        if (trayNotify == IntPtr.Zero)
            return false;

        if (!GetWindowRect(taskbarHwnd, out tbRect) || !GetWindowRect(trayNotify, out tnRect))
            return false;

        double dpiScale = TrayIconRenderer.GetWindowDpiScale(taskbarHwnd);
        int tbWidth = tbRect.Right - tbRect.Left;
        int tbHeight = tbRect.Bottom - tbRect.Top;
        int minWidth = (int)Math.Round(300 * dpiScale);
        int minHeight = Math.Max(24, (int)Math.Round(24 * dpiScale));
        int maxHeight = (int)Math.Round(160 * dpiScale);
        int minTrayLeftGap = (int)Math.Round(80 * dpiScale);

        if (tbWidth < minWidth || tbHeight < minHeight || tbHeight > maxHeight || tnRect.Left <= tbRect.Left + minTrayLeftGap)
            return false;

        // Auto-hide taskbar check: when Windows auto-hides the taskbar, Shell_TrayWnd stays "visible"
        // but slides off-screen so only a 1-4px peek edge intersects the monitor bounds.
        IntPtr mon = MonitorFromWindow(taskbarHwnd, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (mon != IntPtr.Zero && GetMonitorInfo(mon, ref mi))
        {
            int visibleTop = Math.Max(tbRect.Top, mi.rcMonitor.Top);
            int visibleBottom = Math.Min(tbRect.Bottom, mi.rcMonitor.Bottom);
            int visibleLeft = Math.Max(tbRect.Left, mi.rcMonitor.Left);
            int visibleRight = Math.Min(tbRect.Right, mi.rcMonitor.Right);

            int visibleHeight = visibleBottom - visibleTop;
            int visibleWidth = visibleRight - visibleLeft;
            if (visibleHeight < Math.Max(16, tbHeight / 2) || visibleWidth < minWidth)
                return false;
        }

        return true;
    }

    private void SyncTaskbarPlacement(bool forceRedraw = false)
    {
        if (_disposed || !_enabled) return;

        bool hasTaskbar = TryGetTaskbarGeometry(out var taskbarHwnd, out var tbRect, out var tnRect);
        bool shouldHide = !hasTaskbar || IsFullscreenBlockingTaskbar();

        if (!shouldHide && (_hwnd == IntPtr.Zero || !IsWindow(_hwnd)))
        {
            if (!EnsureWindowCreated())
            {
                NotifyAttachmentChangedIfNeeded();
                return;
            }
            forceRedraw = true;
        }

        if (shouldHide != _isHiddenForFullscreen)
        {
            _isHiddenForFullscreen = shouldHide;
            if (shouldHide)
            {
                if (_hwnd != IntPtr.Zero && IsWindow(_hwnd))
                    ShowWindow(_hwnd, SW_HIDE);
            }
            else
            {
                // Re-render at exact current taskbar position BEFORE showing (zero position jump)
                forceRedraw = true;
            }
        }

        if (!_isHiddenForFullscreen)
        {
            int tbHeight = tbRect.Bottom - tbRect.Top;
            double dpiScale = TrayIconRenderer.GetWindowDpiScale(taskbarHwnd);
            bool geometryChanged = tnRect.Left != _lastTrayNotifyLeft ||
                                   tbRect.Top != _lastTaskbarTop ||
                                   tbHeight != _lastTaskbarHeight ||
                                   Math.Abs(dpiScale - _lastDpiScale) > 0.01;

            if (forceRedraw || geometryChanged)
            {
                RenderAndPosition();
            }
            else if (_hwnd != IntPtr.Zero && IsWindow(_hwnd))
            {
                SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
        }

        NotifyAttachmentChangedIfNeeded();
    }

    private void NotifyAttachmentChangedIfNeeded()
    {
        bool nowAttached = IsPillModeActive;
        if (nowAttached != _lastReportedAttached)
        {
            _lastReportedAttached = nowAttached;
            AttachmentStateChanged?.Invoke(nowAttached);
        }
    }

    private static (int ScreenX, int ScreenY) ComputePillScreenPosition(
        FlyoutPositioner.RECT tbRect,
        FlyoutPositioner.RECT tnRect,
        int pillWidth,
        int pillHeight,
        double dpiScale)
    {
        int tbHeight = tbRect.Bottom - tbRect.Top;
        int marginRight = (int)Math.Round(4.0 * dpiScale);
        int screenX = Math.Max(tbRect.Left + 8, tnRect.Left - pillWidth - marginRight);
        int screenY = tbRect.Top + Math.Max(0, (tbHeight - pillHeight) / 2);
        return (screenX, screenY);
    }

    private void RenderAndPosition()
    {
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd))
            return;

        if (!TryGetTaskbarGeometry(out var taskbarHwnd, out var tbRect, out var tnRect))
            return;

        int tbHeight = tbRect.Bottom - tbRect.Top;
        double dpiScale = TrayIconRenderer.GetWindowDpiScale(taskbarHwnd);
        _lastTrayNotifyLeft = tnRect.Left;
        _lastTaskbarTop = tbRect.Top;
        _lastTaskbarHeight = tbHeight;
        _lastDpiScale = dpiScale;

        using var bmp = TrayIconRenderer.RenderTaskbarPillBitmap(
            _lastState, _currentStyle, dpiScale, _isHovered, _isPressed);

        var (screenX, screenY) = ComputePillScreenPosition(tbRect, tnRect, bmp.Width, bmp.Height, dpiScale);
        PushBitmapToLayeredWindow(bmp, screenX, screenY);

        if (!_isHiddenForFullscreen)
        {
            SetWindowPos(_hwnd, HWND_TOPMOST, screenX, screenY, bmp.Width, bmp.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
        else
        {
            ShowWindow(_hwnd, SW_HIDE);
        }
    }

    private void RenderHoverOnly()
    {
        if (_hwnd == IntPtr.Zero || !IsWindow(_hwnd) || _isHiddenForFullscreen)
            return;

        if (!TryGetTaskbarGeometry(out var taskbarHwnd, out var tbRect, out var tnRect))
            return;

        double dpiScale = TrayIconRenderer.GetWindowDpiScale(taskbarHwnd);
        using var bmp = TrayIconRenderer.RenderTaskbarPillBitmap(
            _lastState, _currentStyle, dpiScale, _isHovered, _isPressed);

        var (screenX, screenY) = ComputePillScreenPosition(tbRect, tnRect, bmp.Width, bmp.Height, dpiScale);
        PushBitmapToLayeredWindow(bmp, screenX, screenY);
    }

    private void PushBitmapToLayeredWindow(Bitmap premultipliedBmp, int screenX, int screenY)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return;

        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;

        try
        {
            hBitmap = premultipliedBmp.GetHbitmap(Color.FromArgb(0));
            oldBitmap = SelectObject(memDc, hBitmap);

            var size = new SIZE { cx = premultipliedBmp.Width, cy = premultipliedBmp.Height };
            var srcPt = new FlyoutPositioner.POINT { X = 0, Y = 0 };
            var dstPt = new FlyoutPositioner.POINT { X = screenX, Y = screenY };
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA
            };

            UpdateLayeredWindow(_hwnd, screenDc, ref dstPt, ref size, memDc, ref srcPt, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero) SelectObject(memDc, oldBitmap);
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private bool IsFullscreenBlockingTaskbar()
    {
        try
        {
            if (SHQueryUserNotificationState(out var state) == 0 &&
                state is QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN
                      or QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE)
            {
                return true;
            }

            IntPtr tb = FindWindowW("Shell_TrayWnd", null);
            if (tb == IntPtr.Zero || !GetWindowRect(tb, out var tbRect))
                return false;

            IntPtr mon = MonitorFromWindow(tb, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(mon, ref mi))
                return false;

            // 1. Probe the center of TrayNotifyWnd (clock/system icons), which our pill NEVER covers.
            // If Shell_TrayWnd itself is the top-most window there, the taskbar is genuinely visible.
            IntPtr trayNotify = FindWindowExW(tb, IntPtr.Zero, "TrayNotifyWnd", null);
            FlyoutPositioner.RECT probeRect =
                trayNotify != IntPtr.Zero && GetWindowRect(trayNotify, out var tnRect) && tnRect.Right > tnRect.Left
                    ? tnRect
                    : tbRect;

            var probePt = new FlyoutPositioner.POINT
            {
                X = probeRect.Left + (probeRect.Right - probeRect.Left) / 2,
                Y = probeRect.Top + (probeRect.Bottom - probeRect.Top) / 2
            };

            IntPtr hit = WindowFromPoint(probePt);
            if (hit != IntPtr.Zero)
            {
                IntPtr hitRoot = GetAncestor(hit, GA_ROOT);
                if (hitRoot == tb || hit == tb)
                    return false;

                // Another window is covering the taskbar tray area (e.g. Discord, YouTube/Chrome F11,
                // borderless fullscreen game — even if the user clicked on a second monitor).
                if (hitRoot != IntPtr.Zero && hitRoot != _hwnd && IsFullscreenWindowOnMonitor(hitRoot, mi.rcMonitor))
                    return true;
            }

            // 2. Fallback: check whether the current foreground window covers the entire taskbar monitor.
            IntPtr fg = GetForegroundWindow();
            if (fg != IntPtr.Zero && fg != _hwnd && fg != tb && IsFullscreenWindowOnMonitor(fg, mi.rcMonitor))
                return true;
        }
        catch { }

        return false;
    }

    private static bool IsFullscreenWindowOnMonitor(IntPtr hwnd, FlyoutPositioner.RECT monRect)
    {
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r))
            return false;

        if (hwnd == FindWindowW("Progman", null) || hwnd == FindWindowW("WorkerW", null))
            return false;

        // 2px tolerance for borderless Chromium/Electron fullscreen windows (Discord, Chrome, Edge)
        return r.Left <= monRect.Left + 2 &&
               r.Top <= monRect.Top + 2 &&
               r.Right >= monRect.Right - 2 &&
               r.Bottom >= monRect.Bottom - 2;
    }

    private void EnsureWindowClassRegistered()
    {
        IntPtr hInst = GetModuleHandleW(null);
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            style = CS_DBLCLKS,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            hInstance = hInst,
            hCursor = LoadCursorW(IntPtr.Zero, IDC_HAND),
            lpszClassName = WindowClassName
        };
        RegisterClassExW(ref wc);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_SETCURSOR:
                SetCursor(LoadCursorW(IntPtr.Zero, IDC_HAND));
                return new IntPtr(1);

            case WM_MOUSEMOVE:
                if (!_isTrackingLeave)
                {
                    var tme = new TRACKMOUSEEVENT
                    {
                        cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(),
                        dwFlags = TME_LEAVE,
                        hwndTrack = hWnd
                    };
                    TrackMouseEvent(ref tme);
                    _isTrackingLeave = true;
                }
                if (!_isHovered)
                {
                    _isHovered = true;
                    RenderHoverOnly();
                }
                return IntPtr.Zero;

            case WM_MOUSELEAVE:
                _isTrackingLeave = false;
                if (_isHovered || _isPressed)
                {
                    _isHovered = false;
                    _isPressed = false;
                    RenderHoverOnly();
                }
                return IntPtr.Zero;

            case WM_LBUTTONDOWN:
            case WM_LBUTTONDBLCLK:
                _isPressed = true;
                RenderHoverOnly();
                {
                    long now = Environment.TickCount64;
                    if (now - _lastClickTick >= 50)
                    {
                        _lastClickTick = now;
                        LeftClicked?.Invoke();
                    }
                }
                return IntPtr.Zero;

            case WM_LBUTTONUP:
                if (_isPressed)
                {
                    _isPressed = false;
                    RenderHoverOnly();
                }
                return IntPtr.Zero;

            case WM_RBUTTONUP:
            case WM_CONTEXTMENU:
                {
                    long now = Environment.TickCount64;
                    if (now - _lastRightClickTick >= 120)
                    {
                        _lastRightClickTick = now;
                        RightClicked?.Invoke();
                    }
                }
                return IntPtr.Zero;
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _enabled = false;
        _layoutWatchTimer.Stop();

        if (_hwnd != IntPtr.Zero && IsWindow(_hwnd))
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }
}
