using System.Runtime.InteropServices;

namespace MouseDeck.Win32.Automation;

public sealed record WindowsMouseSettings(
    int PointerSpeed,           // 1..20 (10 = 6/11 standard 1:1)
    bool EnhancePointerPrecision,
    int WheelScrollLines,       // 1..100 or -1 (one screen at a time)
    uint DoubleClickTimeMs);    // 200..900 ms

/// <summary>
/// Reads live Windows OS pointer settings via SystemParametersInfo without modifying anything on startup.
/// Writes changes ONLY when the user explicitly alters a Windows setting in the UI.
/// </summary>
public static class WindowsMouseSettingsService
{
    private const uint SPI_GETMOUSE = 0x0003;
    private const uint SPI_SETMOUSE = 0x0004;
    private const uint SPI_GETWHEELSCROLLLINES = 0x0068;
    private const uint SPI_SETWHEELSCROLLLINES = 0x0069;
    private const uint SPI_GETMOUSESPEED = 0x0070;
    private const uint SPI_SETMOUSESPEED = 0x0071;

    private const uint SPIF_UPDATEINIFILE = 0x01;
    private const uint SPIF_SENDCHANGE = 0x02;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, ref int pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, int[] pvParam, uint fWinIni);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDoubleClickTime(uint uInterval);

    public static WindowsMouseSettings ReadCurrent()
    {
        int speed = 10;
        _ = SystemParametersInfoW(SPI_GETMOUSESPEED, 0, ref speed, 0);

        var mouseParams = new int[3];
        bool epp = false;
        if (SystemParametersInfoW(SPI_GETMOUSE, 0, mouseParams, 0))
            epp = mouseParams[2] != 0;

        int scrollLines = 3;
        _ = SystemParametersInfoW(SPI_GETWHEELSCROLLLINES, 0, ref scrollLines, 0);

        uint dblClick = GetDoubleClickTime();
        if (dblClick == 0) dblClick = 500;

        return new WindowsMouseSettings(
            Math.Clamp(speed, 1, 20),
            epp,
            scrollLines,
            Math.Clamp(dblClick, 200u, 900u));
    }

    public static bool SetPointerSpeed(int speed)
    {
        int clamped = Math.Clamp(speed, 1, 20);
        return SystemParametersInfoW(SPI_SETMOUSESPEED, 0, new IntPtr(clamped), SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
    }

    public static bool SetEnhancePointerPrecision(bool enabled)
    {
        var mouseParams = new int[3];
        if (!SystemParametersInfoW(SPI_GETMOUSE, 0, mouseParams, 0))
        {
            mouseParams[0] = 6;
            mouseParams[1] = 10;
        }
        mouseParams[2] = enabled ? 1 : 0;
        return SystemParametersInfoW(SPI_SETMOUSE, 0, mouseParams, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
    }

    public static bool SetWheelScrollLines(int lines)
    {
        uint val = lines < 0 ? 0xFFFFFFFF : (uint)Math.Clamp(lines, 1, 100);
        return SystemParametersInfoW(SPI_SETWHEELSCROLLLINES, val, IntPtr.Zero, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
    }

    public static bool SetDoubleClickTimeMs(uint ms)
    {
        return SetDoubleClickTime(Math.Clamp(ms, 200u, 900u));
    }
}
