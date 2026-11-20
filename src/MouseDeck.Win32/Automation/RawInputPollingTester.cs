using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace MouseDeck.Win32.Automation;

/// <summary>
/// On-demand live mouse polling-rate (Hz) tester using native Win32 Raw Input (WM_INPUT / RIM_TYPEMOUSE).
/// Works with literally ANY mouse on Windows (125..8000 Hz). Consumes 0% CPU when stopped.
/// </summary>
public sealed class RawInputPollingTester : IDisposable
{
    private const int WM_INPUT = 0x00FF;
    private const ushort HID_USAGE_PAGE_GENERIC = 0x01;
    private const ushort HID_USAGE_GENERIC_MOUSE = 0x02;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RIDEV_REMOVE = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    private HwndSource? _hwndSource;
    private readonly long[] _ringTimestamps = new long[512];
    private int _ringHead;
    private int _ringCount;
    private long _lastEmitTick;
    private int _peakHz;
    private bool _isRunning;
    private bool _disposed;

    public event Action<int, int>? PollingRateUpdated; // (currentHz, peakHz)

    public bool IsRunning => _isRunning;

    public void Start()
    {
        if (_disposed || _isRunning) return;

        var parameters = new HwndSourceParameters("MouseDeck_RawInputTester")
        {
            Width = 0,
            Height = 0,
            PositionX = -32000,
            PositionY = -32000,
            WindowStyle = 0,
            ExtendedWindowStyle = 0x00000080
        };

        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProc);

        _ringHead = 0;
        _ringCount = 0;
        _peakHz = 0;
        _lastEmitTick = Environment.TickCount64;

        var devices = new[]
        {
            new RAWINPUTDEVICE
            {
                usUsagePage = HID_USAGE_PAGE_GENERIC,
                usUsage = HID_USAGE_GENERIC_MOUSE,
                dwFlags = RIDEV_INPUTSINK,
                hwndTarget = _hwndSource.Handle
            }
        };

        if (RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
        {
            _isRunning = true;
        }
        else
        {
            Stop();
        }
    }

    public void Stop()
    {
        if (!_isRunning && _hwndSource is null) return;
        _isRunning = false;

        try
        {
            var devices = new[]
            {
                new RAWINPUTDEVICE
                {
                    usUsagePage = HID_USAGE_PAGE_GENERIC,
                    usUsage = HID_USAGE_GENERIC_MOUSE,
                    dwFlags = RIDEV_REMOVE,
                    hwndTarget = IntPtr.Zero
                }
            };
            RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        }
        catch { }

        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource.Dispose();
            _hwndSource = null;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_INPUT && _isRunning)
        {
            long ts = Stopwatch.GetTimestamp();
            _ringTimestamps[_ringHead] = ts;
            _ringHead = (_ringHead + 1) % _ringTimestamps.Length;
            if (_ringCount < _ringTimestamps.Length) _ringCount++;

            long nowTick = Environment.TickCount64;
            if (nowTick - _lastEmitTick >= 100)
            {
                _lastEmitTick = nowTick;
                int hz = ComputeWindowHz(ts);
                if (hz > _peakHz) _peakHz = hz;
                PollingRateUpdated?.Invoke(hz, _peakHz);
            }
        }

        return IntPtr.Zero;
    }

    private int ComputeWindowHz(long nowTs)
    {
        if (_ringCount < 4) return 0;

        // Count events within the trailing 250ms window for crisp, high-frequency accuracy (125..8000 Hz)
        long windowTicks = Stopwatch.Frequency / 4; // 250ms
        long cutoff = nowTs - windowTicks;
        int eventsInWindow = 0;
        long oldestInWindow = nowTs;

        for (int i = 0; i < _ringCount; i++)
        {
            int idx = (_ringHead - 1 - i + _ringTimestamps.Length) % _ringTimestamps.Length;
            long t = _ringTimestamps[idx];
            if (t < cutoff) break;
            eventsInWindow++;
            oldestInWindow = t;
        }

        if (eventsInWindow < 3) return 0;

        double spanSec = (double)(nowTs - oldestInWindow) / Stopwatch.Frequency;
        if (spanSec < 0.015) return 0;

        int rawHz = (int)Math.Round((eventsInWindow - 1) / spanSec);
        return Math.Clamp(rawHz, 0, 9000);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
