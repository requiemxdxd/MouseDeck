using System.Runtime.InteropServices;
using System.Windows;

namespace MouseDeck.Win32.Windowing;

public static class FlyoutPositioner
{
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForSystem();

    private const int ENUM_CURRENT_SETTINGS = -1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODEW
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsW(string? lpszDeviceName, int iModeNum, ref DEVMODEW lpDevMode);

    /// <summary>
    /// Queries the current primary monitor refresh rate (60..240 Hz) so WPF animations
    /// tick smoothly at native high-refresh rates (144Hz/165Hz/240Hz) instead of default 60Hz.
    /// </summary>
    public static int GetDisplayRefreshRateHz()
    {
        try
        {
            var dm = new DEVMODEW { dmSize = (ushort)Marshal.SizeOf<DEVMODEW>() };
            if (EnumDisplaySettingsW(null, ENUM_CURRENT_SETTINGS, ref dm) && dm.dmDisplayFrequency >= 60)
            {
                return (int)Math.Clamp(dm.dmDisplayFrequency, 60u, 240u);
            }
        }
        catch { }

        return 60;
    }

    /// <summary>
    /// Computes native Windows 11 Flyout DIP coordinates directly above the tray icon (or cursor monitor work area),
    /// clamped within the visible work area with a 12px margin.
    /// </summary>
    public static (double Left, double Top) ComputePosition(double flyoutWidthDip, double flyoutHeightDip, RECT? trayIconRect)
    {
        POINT anchorPx;
        if (trayIconRect is { } r && (r.Right > r.Left || r.Bottom > r.Top))
        {
            anchorPx = new POINT
            {
                X = (r.Left + r.Right) / 2,
                Y = (r.Top + r.Bottom) / 2
            };
        }
        else
        {
            GetCursorPos(out anchorPx);
        }

        IntPtr hMonitor = MonitorFromPoint(anchorPx, MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(hMonitor, ref mi))
        {
            var wa = SystemParameters.WorkArea;
            return (Math.Max(12, wa.Right - flyoutWidthDip - 12), Math.Max(12, wa.Bottom - flyoutHeightDip - 12));
        }

        double scale = 1.0;
        if (GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0)
        {
            scale = dpiX / 96.0;
        }

        double workLeft = mi.rcWork.Left / scale;
        double workTop = mi.rcWork.Top / scale;
        double workRight = mi.rcWork.Right / scale;
        double workBottom = mi.rcWork.Bottom / scale;

        const double margin = 12.0;

        // Center horizontally on tray icon when known, otherwise snap to bottom-right near Win11 system tray
        double desiredLeft = trayIconRect.HasValue
            ? (anchorPx.X / scale) - (flyoutWidthDip / 2.0)
            : workRight - flyoutWidthDip - margin;

        // Clamp within screen work area
        double left = Math.Clamp(desiredLeft, workLeft + margin, Math.Max(workLeft + margin, workRight - flyoutWidthDip - margin));

        // Detect top vs bottom taskbar (also respects auto-hide taskbars where rcWork == rcMonitor)
        int monitorMidY = (mi.rcMonitor.Top + mi.rcMonitor.Bottom) / 2;
        bool taskbarAtTop = mi.rcWork.Top > mi.rcMonitor.Top ||
                            (trayIconRect.HasValue && anchorPx.Y < monitorMidY);

        double top;
        if (taskbarAtTop)
        {
            double topAnchor = trayIconRect is { } tr
                ? Math.Max(workTop, tr.Bottom / scale)
                : workTop;
            top = topAnchor + margin;
        }
        else
        {
            double bottomAnchor = trayIconRect is { } br
                ? Math.Min(workBottom, br.Top / scale)
                : workBottom;
            top = bottomAnchor - flyoutHeightDip - margin;
        }

        top = Math.Clamp(top, workTop + margin, Math.Max(workTop + margin, workBottom - flyoutHeightDip - margin));

        // Snap DIP coordinates to exact physical device pixels so WPF never renders text at sub-pixel offsets
        left = Math.Round(left * scale) / scale;
        top = Math.Round(top * scale) / scale;
        return (left, top);
    }
}
