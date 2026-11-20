using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using MouseDeck.Core.Models;

namespace MouseDeck.Win32.Windowing;

public static class DwmBackdropHelper
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    private const int DWMWCP_ROUND = 2;
    private const int DWMSBT_MAINWINDOW = 2;      // Mica
    private const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const long WS_EX_APPWINDOW = 0x00040000L;
    private const long WS_EX_LAYERED = 0x00080000L;
    private const uint LWA_ALPHA = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarInset);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    /// <summary>
    /// Reliably activates a system tray flyout window even when Windows foreground lock is active.
    /// </summary>
    public static void ForceForegroundWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;
        if (GetForegroundWindow() == hWnd) return;

        if (SetForegroundWindow(hWnd))
            return;

        IntPtr fg = GetForegroundWindow();
        uint currentThread = GetCurrentThreadId();
        uint fgThread = fg != IntPtr.Zero ? GetWindowThreadProcessId(fg, out _) : 0;

        if (fgThread != 0 && fgThread != currentThread)
        {
            AttachThreadInput(currentThread, fgThread, true);
            SetForegroundWindow(hWnd);
            BringWindowToTop(hWnd);
            AttachThreadInput(currentThread, fgThread, false);
        }
        else
        {
            SetForegroundWindow(hWnd);
            BringWindowToTop(hWnd);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    /// <summary>
    /// Applies native Windows 11 dark rounded corners, hairline border, and hardware DWM drop shadow
    /// on an opaque HWND (no AllowsTransparency) so WPF renders with full 3x RGB Subpixel ClearType.
    /// </summary>
    public static void ApplyCrispWin11Chrome(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        HideFromAltTab(hwnd);

        int dark = 1;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        int corner = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        // Crisp subtle Win11 graphite hairline border (BGR COLORREF: #2A2A2D)
        int borderColor = 0x002D2A2A;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));
    }

    /// <summary>
    /// Hardware-accelerated DWM constant window opacity (0..255) for smooth open/close fades
    /// without disabling WPF RGB Subpixel ClearType. Strips WS_EX_LAYERED automatically at 255.
    /// </summary>
    public static void SetWindowAlpha(IntPtr hwnd, byte alpha)
    {
        if (hwnd == IntPtr.Zero) return;

        long style = IntPtr.Size == 8
            ? GetWindowLongPtr64(hwnd, GWL_EXSTYLE).ToInt64()
            : GetWindowLong32(hwnd, GWL_EXSTYLE);

        if (alpha >= 255)
        {
            if ((style & WS_EX_LAYERED) != 0)
            {
                long next = style & ~WS_EX_LAYERED;
                if (IntPtr.Size == 8) SetWindowLongPtr64(hwnd, GWL_EXSTYLE, new IntPtr(next));
                else SetWindowLong32(hwnd, GWL_EXSTYLE, (int)next);
            }
            return;
        }

        if ((style & WS_EX_LAYERED) == 0)
        {
            long next = style | WS_EX_LAYERED;
            if (IntPtr.Size == 8) SetWindowLongPtr64(hwnd, GWL_EXSTYLE, new IntPtr(next));
            else SetWindowLong32(hwnd, GWL_EXSTYLE, (int)next);
        }

        SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
    }

    public static void ApplyWin11FlyoutChrome(Window window, FlyoutBackdropStyle backdrop)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        HideFromAltTab(hwnd);

        // Dark mode title/backdrop palette
        int dark = 1;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        // Native Windows 11 rounded corners
        int corner = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        // Subtle crisp dark border (BGR COLORREF)
        int borderColor = 0x003A3A3A;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));

        // Extend DWM glass frame into client area so Acrylic/Mica shines through transparent background
        var margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
        _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);

        ApplyBackdrop(hwnd, backdrop);
    }

    public static void ApplyBackdrop(IntPtr hwnd, FlyoutBackdropStyle backdrop)
    {
        if (hwnd == IntPtr.Zero) return;
        int backdropType = backdrop == FlyoutBackdropStyle.Mica ? DWMSBT_MAINWINDOW : DWMSBT_TRANSIENTWINDOW;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
    }

    public static void HideFromAltTab(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        if (IntPtr.Size == 8)
        {
            long style = GetWindowLongPtr64(hwnd, GWL_EXSTYLE).ToInt64();
            style |= WS_EX_TOOLWINDOW;
            style &= ~WS_EX_APPWINDOW;
            SetWindowLongPtr64(hwnd, GWL_EXSTYLE, new IntPtr(style));
        }
        else
        {
            int style = GetWindowLong32(hwnd, GWL_EXSTYLE);
            style |= (int)WS_EX_TOOLWINDOW;
            style &= ~(int)WS_EX_APPWINDOW;
            SetWindowLong32(hwnd, GWL_EXSTYLE, style);
        }
    }
}
