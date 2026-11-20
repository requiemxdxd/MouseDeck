using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using MouseDeck.Core.Models;
using MouseDeck.Core.Transport;

namespace MouseDeck.Win32.Hid;

public sealed partial class Win32HidTransport : IHidTransport
{
    private static readonly IntPtr InvalidHandleValue = new(-1);

    private static readonly (string ProcessName, string FriendlyName)[] KnownVendorApps =
    [
        ("lghub_agent", "Logitech G HUB"),
        ("logioptionsplus_agent", "Logi Options+"),
        ("Razer Synapse 3", "Razer Synapse 3"),
        ("RazerAppEngine", "Razer Synapse 4"),
        ("SteelSeriesGG", "SteelSeries GG"),
        ("iCUE", "Corsair iCUE"),
        ("NGENUITY2", "HyperX NGENUITY"),
        ("ATK HUB", "ATK V HUB"),
        ("VXE HUB", "VXE HUB")
    ];

    [GeneratedRegex(@"mi_([0-9a-fA-F]{2})", RegexOptions.IgnoreCase)]
    private static partial Regex MiRegex();

    public IReadOnlyList<MouseDeviceInfo> EnumerateDevices()
    {
        var results = new List<MouseDeviceInfo>();
        NativeHidApi.HidD_GetHidGuid(out var hidGuid);

        IntPtr devInfoSet = NativeHidApi.SetupDiGetClassDevsW(
            ref hidGuid,
            null,
            IntPtr.Zero,
            NativeHidApi.DIGCF_PRESENT | NativeHidApi.DIGCF_DEVICEINTERFACE);

        if (devInfoSet == IntPtr.Zero || devInfoSet == InvalidHandleValue)
            return results;

        try
        {
            var ifaceData = new NativeHidApi.SP_DEVICE_INTERFACE_DATA
            {
                cbSize = Marshal.SizeOf<NativeHidApi.SP_DEVICE_INTERFACE_DATA>()
            };

            uint index = 0;
            while (NativeHidApi.SetupDiEnumDeviceInterfaces(devInfoSet, IntPtr.Zero, ref hidGuid, index++, ref ifaceData))
            {
                var path = GetDevicePath(devInfoSet, ref ifaceData);
                if (string.IsNullOrWhiteSpace(path))
                    continue;

                var info = QueryDeviceMetadata(path);
                if (info is not null)
                    results.Add(info);
            }
        }
        finally
        {
            NativeHidApi.SetupDiDestroyDeviceInfoList(devInfoSet);
        }

        return results;
    }

    public Task<IReadOnlyList<MouseDeviceInfo>> EnumerateBluetoothMiceAsync(CancellationToken ct = default)
        => BluetoothMouseEnumerator.EnumerateAsync(ct);

    public string? DetectRunningVendorSoftware()
    {
        try
        {
            foreach (var (procName, friendly) in KnownVendorApps)
            {
                var procs = Process.GetProcessesByName(procName);
                bool running = procs.Length > 0;
                foreach (var p in procs) p.Dispose();
                if (running) return friendly;
            }
        }
        catch { }

        return null;
    }

    public Task<IHidSession?> OpenDeviceAsync(MouseDeviceInfo device, CancellationToken ct = default)
    {
        if (device.IsBluetooth)
        {
            return Task.FromResult<IHidSession?>(new BluetoothHidSession(device));
        }

        return Task.Run<IHidSession?>(() =>
        {
            var handle = NativeHidApi.CreateFileW(
                device.DevicePath,
                NativeHidApi.GENERIC_READ | NativeHidApi.GENERIC_WRITE,
                NativeHidApi.FILE_SHARE_READ | NativeHidApi.FILE_SHARE_WRITE,
                IntPtr.Zero,
                NativeHidApi.OPEN_EXISTING,
                NativeHidApi.FILE_FLAG_OVERLAPPED,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                handle.Dispose();
                // Fallback: some Feature-only or metadata-only interfaces allow DesiredAccess = 0 with shared access
                handle = NativeHidApi.CreateFileW(
                    device.DevicePath,
                    0,
                    NativeHidApi.FILE_SHARE_READ | NativeHidApi.FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    NativeHidApi.OPEN_EXISTING,
                    NativeHidApi.FILE_FLAG_OVERLAPPED,
                    IntPtr.Zero);

                if (handle.IsInvalid)
                {
                    handle.Dispose();
                    return null;
                }
            }

            return new Win32HidSession(device, handle);
        }, ct);
    }

    private static string? GetDevicePath(IntPtr devInfoSet, ref NativeHidApi.SP_DEVICE_INTERFACE_DATA ifaceData)
    {
        NativeHidApi.SetupDiGetDeviceInterfaceDetailW(devInfoSet, ref ifaceData, IntPtr.Zero, 0, out int requiredSize, IntPtr.Zero);
        if (requiredSize <= 4)
            return null;

        IntPtr buffer = Marshal.AllocHGlobal(requiredSize);
        try
        {
            int cbSize = IntPtr.Size == 8 ? 8 : 6;
            Marshal.WriteInt32(buffer, cbSize);

            if (!NativeHidApi.SetupDiGetDeviceInterfaceDetailW(devInfoSet, ref ifaceData, buffer, requiredSize, out _, IntPtr.Zero))
                return null;

            return Marshal.PtrToStringUni(IntPtr.Add(buffer, 4));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static MouseDeviceInfo? QueryDeviceMetadata(string devicePath)
    {
        using var queryHandle = NativeHidApi.CreateFileW(
            devicePath,
            0,
            NativeHidApi.FILE_SHARE_READ | NativeHidApi.FILE_SHARE_WRITE,
            IntPtr.Zero,
            NativeHidApi.OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (queryHandle.IsInvalid)
            return null;

        var attrs = new NativeHidApi.HIDD_ATTRIBUTES { Size = Marshal.SizeOf<NativeHidApi.HIDD_ATTRIBUTES>() };
        if (!NativeHidApi.HidD_GetAttributes(queryHandle, ref attrs))
            return null;

        ushort usagePage = 0;
        ushort usage = 0;
        int inLen = 0, outLen = 0, featLen = 0;

        if (NativeHidApi.HidD_GetPreparsedData(queryHandle, out IntPtr preparsed) && preparsed != IntPtr.Zero)
        {
            try
            {
                if (NativeHidApi.HidP_GetCaps(preparsed, out var caps) > 0)
                {
                    usagePage = caps.UsagePage;
                    usage = caps.Usage;
                    inLen = caps.InputReportByteLength;
                    outLen = caps.OutputReportByteLength;
                    featLen = caps.FeatureReportByteLength;
                }
            }
            finally
            {
                NativeHidApi.HidD_FreePreparsedData(preparsed);
            }
        }

        string product = ReadHidString(queryHandle, NativeHidApi.HidD_GetProductString);
        string manufacturer = ReadHidString(queryHandle, NativeHidApi.HidD_GetManufacturerString);
        string serial = ReadHidString(queryHandle, NativeHidApi.HidD_GetSerialNumberString);

        bool canReadWrite = false;
        if (!(usagePage == 0x0001 && (usage == 0x0002 || usage == 0x0006)))
        {
            using var rwTest = NativeHidApi.CreateFileW(
                devicePath,
                NativeHidApi.GENERIC_READ | NativeHidApi.GENERIC_WRITE,
                NativeHidApi.FILE_SHARE_READ | NativeHidApi.FILE_SHARE_WRITE,
                IntPtr.Zero,
                NativeHidApi.OPEN_EXISTING,
                0,
                IntPtr.Zero);
            canReadWrite = !rwTest.IsInvalid;
        }

        int? mi = null;
        var match = MiRegex().Match(devicePath);
        if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int miVal))
        {
            mi = miVal;
        }

        return new MouseDeviceInfo
        {
            DevicePath = devicePath,
            VendorId = attrs.VendorID,
            ProductId = attrs.ProductID,
            VersionNumber = attrs.VersionNumber,
            UsagePage = usagePage,
            Usage = usage,
            InterfaceNumber = mi,
            InputReportLength = inLen,
            OutputReportLength = outLen,
            FeatureReportLength = featLen,
            ProductName = string.IsNullOrWhiteSpace(product) ? $"HID {attrs.VendorID:X4}:{attrs.ProductID:X4}" : product,
            ManufacturerName = manufacturer,
            SerialNumber = serial,
            CanOpenReadWrite = canReadWrite
        };
    }

    private static string ReadHidString(SafeFileHandle handle, Func<SafeFileHandle, byte[], int, bool> reader)
    {
        var buf = new byte[256];
        if (!reader(handle, buf, buf.Length))
            return string.Empty;

        var text = Encoding.Unicode.GetString(buf);
        int nullIdx = text.IndexOf('\0');
        return (nullIdx >= 0 ? text[..nullIdx] : text).Trim();
    }

    private sealed class BluetoothHidSession(MouseDeviceInfo device) : IHidSession
    {
        public MouseDeviceInfo Device { get; } = device;
        public bool IsOpen => true;

        public Task<bool> SetFeatureReportAsync(ReadOnlyMemory<byte> report, CancellationToken ct = default) => Task.FromResult(false);
        public Task<byte[]?> GetFeatureReportAsync(byte reportId, int expectedLength, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);
        public Task<bool> WriteOutputReportAsync(ReadOnlyMemory<byte> report, int timeoutMs = 250, CancellationToken ct = default) => Task.FromResult(false);
        public Task<byte[]?> ReadInputReportAsync(int expectedLength, int timeoutMs = 250, CancellationToken ct = default) => Task.FromResult<byte[]?>(null);

        public async Task<int?> TryReadStandardHidBatteryAsync(CancellationToken ct = default)
        {
            if (Device.OsBatteryPercent.HasValue)
                return Device.OsBatteryPercent;

            return await BluetoothMouseEnumerator.TryReadGattBatteryAsync(Device.DevicePath, ct);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Win32HidSession : IHidSession
    {
        private readonly SafeFileHandle _handle;
        private bool _disposed;

        public Win32HidSession(MouseDeviceInfo device, SafeFileHandle handle)
        {
            Device = device;
            _handle = handle;
        }

        public MouseDeviceInfo Device { get; }
        public bool IsOpen => !_disposed && !_handle.IsInvalid && !_handle.IsClosed;

        public void FlushInputQueue()
        {
            if (!IsOpen) return;
            try { NativeHidApi.HidD_FlushQueue(_handle); } catch { }
        }

        public Task<bool> SetFeatureReportAsync(ReadOnlyMemory<byte> report, CancellationToken ct = default)
        {
            return Task.Run(() =>
            {
                if (!IsOpen) return false;
                var buf = report.ToArray();
                return NativeHidApi.HidD_SetFeature(_handle, buf, buf.Length);
            }, ct);
        }

        public Task<byte[]?> GetFeatureReportAsync(byte reportId, int expectedLength, CancellationToken ct = default)
        {
            return Task.Run<byte[]?>(() =>
            {
                if (!IsOpen) return null;
                int len = Math.Max(expectedLength, Math.Max(Device.FeatureReportLength, 2));
                var buf = new byte[len];
                buf[0] = reportId;
                return NativeHidApi.HidD_GetFeature(_handle, buf, buf.Length) ? buf : null;
            }, ct);
        }

        public Task<bool> WriteOutputReportAsync(ReadOnlyMemory<byte> report, int timeoutMs = 250, CancellationToken ct = default)
        {
            return Task.Run(() => WriteOverlapped(report.Span, timeoutMs), ct);
        }

        public Task<byte[]?> ReadInputReportAsync(int expectedLength, int timeoutMs = 250, CancellationToken ct = default)
        {
            return Task.Run(() => ReadOverlapped(expectedLength, timeoutMs), ct);
        }

        public Task<int?> TryReadStandardHidBatteryAsync(CancellationToken ct = default)
        {
            return Task.Run<int?>(() =>
            {
                if (!IsOpen) return null;
                if (!NativeHidApi.HidD_GetPreparsedData(_handle, out IntPtr preparsed) || preparsed == IntPtr.Zero)
                    return null;

                try
                {
                    // Probe standard HID Battery Strength (UsagePage 0x0006 Usage 0x0020) and UPS/Battery RemainingCapacity (0x0085 Usage 0x0066)
                    if (Device.FeatureReportLength >= 2)
                    {
                        var feat = new byte[Device.FeatureReportLength];
                        for (byte rid = 0; rid <= 8; rid++)
                        {
                            feat[0] = rid;
                            if (!NativeHidApi.HidD_GetFeature(_handle, feat, feat.Length))
                                continue;

                            if (TryExtractStandardUsage(preparsed, NativeHidApi.HidP_Feature, feat, out int pct))
                                return pct;
                        }
                    }

                    return null;
                }
                finally
                {
                    NativeHidApi.HidD_FreePreparsedData(preparsed);
                }
            }, ct);
        }

        private static bool TryExtractStandardUsage(IntPtr preparsed, int reportType, byte[] report, out int percent)
        {
            percent = 0;
            (ushort Page, ushort Usage)[] standardUsages =
            [
                (0x0006, 0x0020), // Generic Device Controls -> Battery Strength
                (0x0085, 0x0066), // Battery System -> RemainingCapacity
                (0x0085, 0x0065)  // Battery System -> AbsoluteStateOfCharge
            ];

            foreach (var (page, usage) in standardUsages)
            {
                int status = NativeHidApi.HidP_GetUsageValue(reportType, page, 0, usage, out uint val, preparsed, report, (uint)report.Length);
                if (status == NativeHidApi.HIDP_STATUS_SUCCESS && val is >= 1 and <= 100)
                {
                    percent = (int)val;
                    return true;
                }
            }
            return false;
        }

        private unsafe bool WriteOverlapped(ReadOnlySpan<byte> data, int timeoutMs)
        {
            if (!IsOpen || data.IsEmpty) return false;

            var buffer = data.ToArray();
            IntPtr hEvent = NativeHidApi.CreateEventW(IntPtr.Zero, true, false, null);
            if (hEvent == IntPtr.Zero) return false;

            try
            {
                var overlapped = new NativeHidApi.OVERLAPPED { hEvent = hEvent };
                fixed (byte* pBuf = buffer)
                {
                    bool immediate = NativeHidApi.WriteFile(_handle, pBuf, (uint)buffer.Length, out _, &overlapped);
                    if (immediate) return true;

                    if (Marshal.GetLastWin32Error() != NativeHidApi.ERROR_IO_PENDING)
                        return false;

                    uint wait = NativeHidApi.WaitForSingleObject(hEvent, (uint)Math.Max(timeoutMs, 10));
                    if (wait == NativeHidApi.WAIT_OBJECT_0)
                    {
                        return NativeHidApi.GetOverlappedResult(_handle, &overlapped, out _, false);
                    }

                    NativeHidApi.CancelIoEx(_handle, &overlapped);
                    return false;
                }
            }
            finally
            {
                NativeHidApi.CloseHandle(hEvent);
            }
        }

        private unsafe byte[]? ReadOverlapped(int expectedLength, int timeoutMs)
        {
            if (!IsOpen) return null;

            int len = Math.Max(expectedLength, Math.Max(Device.InputReportLength, 2));
            var buffer = new byte[len];
            IntPtr hEvent = NativeHidApi.CreateEventW(IntPtr.Zero, true, false, null);
            if (hEvent == IntPtr.Zero) return null;

            try
            {
                var overlapped = new NativeHidApi.OVERLAPPED { hEvent = hEvent };
                fixed (byte* pBuf = buffer)
                {
                    bool immediate = NativeHidApi.ReadFile(_handle, pBuf, (uint)buffer.Length, out uint bytesRead, &overlapped);
                    if (immediate)
                        return bytesRead == buffer.Length ? buffer : buffer[..(int)bytesRead];

                    if (Marshal.GetLastWin32Error() != NativeHidApi.ERROR_IO_PENDING)
                        return null;

                    uint wait = NativeHidApi.WaitForSingleObject(hEvent, (uint)Math.Max(timeoutMs, 10));
                    if (wait == NativeHidApi.WAIT_OBJECT_0)
                    {
                        if (NativeHidApi.GetOverlappedResult(_handle, &overlapped, out uint transferred, false))
                            return transferred == buffer.Length ? buffer : buffer[..(int)transferred];
                        return null;
                    }

                    NativeHidApi.CancelIoEx(_handle, &overlapped);
                    return null;
                }
            }
            finally
            {
                NativeHidApi.CloseHandle(hEvent);
            }
        }

        public ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                _handle.Dispose();
            }
            return ValueTask.CompletedTask;
        }
    }
}
