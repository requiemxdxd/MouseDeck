using MouseDeck.Core.Models;
using MouseDeck.Core.Transport;

namespace MouseDeck.Core.Protocols;

/// <summary>
/// Non-intrusive Logitech HID++ 2.0 driver (VID 0x046D) supporting Lightspeed, Bolt, Unifying, and Wired USB.
/// Uses a dedicated Software ID (SwId = 0x0A) so replies never collide with Logitech G HUB / Options+.
/// Dynamically discovers HID++ 2.0 features via IRoot (0x0000):
///   - 0x1004 Unified Battery (G Pro X Superlight 1/2, G502 X, MX Master 3S, etc.)
///   - 0x1000 Battery Status (G Pro Wireless, G703, G903, MX Anywhere)
///   - 0x1001 Battery Voltage (G305, older Lightspeed)
///   - 0x2201 Adjustable DPI (read-only live DPI readout)
/// </summary>
public sealed class LogitechHidPlusPlusProtocol : IMouseProtocol
{
    public const ushort LogitechVendorId = 0x046D;
    public const byte ShortReportId = 0x10;
    public const int ShortReportLength = 7;
    public const byte LongReportId = 0x11;
    public const int LongReportLength = 20;

    // Dedicated Software ID nibble (1..15) so G HUB ignores our replies and we ignore G HUB's traffic
    public const byte SoftwareId = 0x0A;

    private const ushort FeatureUnifiedBattery = 0x1004;
    private const ushort FeatureBatteryStatus = 0x1000;
    private const ushort FeatureBatteryVoltage = 0x1001;
    private const ushort FeatureAdjustableDpi = 0x2201;

    private byte? _cachedDeviceIndex;
    private readonly Dictionary<ushort, byte> _featureIndexCache = [];

    public string ProtocolId => "logitech-hidpp20";
    public string DisplayName => "Logitech HID++ 2.0";
    public int Priority => 85;

    public MouseCapabilities Capabilities { get; } = MouseCapabilities.BatteryOnlyWireless;

    public IReadOnlyList<DeviceMatchRule> SupportedDevices { get; } =
    [
        new(LogitechVendorId, null, 0xFF00, FriendlyModelName: "Logitech Wireless Mouse"),
        new(LogitechVendorId, null, 0xFF43, FriendlyModelName: "Logitech Bolt / Lightspeed")
    ];

    public int MatchScore(MouseDeviceInfo device)
    {
        if (device.IsVirtual || device.IsBluetooth || !device.CanOpenReadWrite)
            return 0;

        if (device.VendorId != LogitechVendorId)
            return 0;

        if (device.UsagePage != 0xFF00 && device.UsagePage != 0xFF43)
            return 0;

        // Prefer the HID++ long report collection (20 bytes) or short report collection (7 bytes)
        int score = 70;
        if (device.OutputReportLength == LongReportLength || device.InputReportLength == LongReportLength)
            score += 35;
        else if (device.OutputReportLength == ShortReportLength || device.InputReportLength == ShortReportLength)
            score += 20;
        else
            return 0;

        if (device.Usage == 0x0002) score += 15; // Long HID++ collection
        else if (device.Usage == 0x0001) score += 5;

        return score;
    }

    public MouseTransportKind DetectTransport(MouseDeviceInfo device)
    {
        var name = device.ProductName.ToLowerInvariant();
        if (name.Contains("receiver") || name.Contains("dongle") || name.Contains("lightspeed") || name.Contains("unifying") || name.Contains("bolt"))
            return MouseTransportKind.Wireless24G;
        return MouseTransportKind.WiredUsb;
    }

    public static byte[] BuildRequest(byte reportId, byte deviceIndex, byte featureIndex, byte functionIndex, ReadOnlySpan<byte> parameters = default)
    {
        int len = reportId == LongReportId ? LongReportLength : ShortReportLength;
        var buf = new byte[len];
        buf[0] = reportId;
        buf[1] = deviceIndex;
        buf[2] = featureIndex;
        buf[3] = (byte)(((functionIndex & 0x0F) << 4) | (SoftwareId & 0x0F));

        int maxParams = len - 4;
        if (!parameters.IsEmpty)
        {
            parameters[..Math.Min(parameters.Length, maxParams)].CopyTo(buf.AsSpan(4));
        }

        return buf;
    }

    public static BatteryReading? ParseUnifiedBatteryResponse(ReadOnlySpan<byte> rx)
    {
        if (rx.Length < 8) return null;
        int pct = rx[4];
        byte chargingStatus = rx[6];
        // Unified Battery 0x1004 chargingStatus: 0=discharging, 1=recharging, 2=almost full, 3=full, 4=slow recharge
        bool isCharging = chargingStatus is 1 or 2 or 3 or 4;

        if (pct == 0 && !isCharging)
            return new BatteryReading(0, false, null, IsMouseAwake: false);

        return new BatteryReading(Math.Clamp(pct, 1, 100), isCharging, null, IsMouseAwake: true);
    }

    public static BatteryReading? ParseLegacyBatteryStatusResponse(ReadOnlySpan<byte> rx)
    {
        if (rx.Length < 7) return null;
        int pct = rx[4];
        byte status = rx[6];
        // 0x1000 status: 0=discharging, 1=recharging, 2=almost full, 3=charged, 4=slow recharge
        bool isCharging = status is 1 or 2 or 3 or 4;
        if (pct == 0 && !isCharging)
            return new BatteryReading(0, false, null, IsMouseAwake: false);

        return new BatteryReading(Math.Clamp(pct, 1, 100), isCharging, null, IsMouseAwake: true);
    }

    public static BatteryReading? ParseBatteryVoltageResponse(ReadOnlySpan<byte> rx)
    {
        if (rx.Length < 7) return null;
        int mv = (rx[4] << 8) | rx[5];
        if (mv < 1000 || mv > 4500)
            return null;

        byte flags = rx[6];
        bool isCharging = (flags & 0x80) != 0;

        // Standard Li-Ion (3.3V..4.2V) vs AA Alkaline (1.1V..1.55V, e.g., G305) curve estimation
        int pct = mv >= 2500
            ? EstimateLiIonPercent(mv)
            : EstimateAlkalinePercent(mv);

        return new BatteryReading(pct, isCharging, mv, IsMouseAwake: true);
    }

    public static int EstimateLiIonPercent(int mv)
    {
        if (mv >= 4160) return 100;
        if (mv <= 3350) return 1;
        return Math.Clamp((int)Math.Round(((mv - 3350.0) / (4160.0 - 3350.0)) * 100.0), 1, 100);
    }

    public static int EstimateAlkalinePercent(int mv)
    {
        if (mv >= 1500) return 100;
        if (mv <= 1100) return 1;
        return Math.Clamp((int)Math.Round(((mv - 1100.0) / (1500.0 - 1100.0)) * 100.0), 1, 100);
    }

    public async Task<bool> PingWakeStateAsync(IHidSession session, Action<HidPacketLogEntry>? log = null, CancellationToken ct = default)
    {
        var devIdx = await ResolveDeviceIndexAsync(session, log, ct);
        return devIdx.HasValue;
    }

    public async Task<BatteryReading?> ReadBatteryAsync(IHidSession session, Action<HidPacketLogEntry>? log = null, CancellationToken ct = default)
    {
        var devIdx = await ResolveDeviceIndexAsync(session, log, ct);
        if (!devIdx.HasValue)
            return new BatteryReading(0, false, null, IsMouseAwake: false);

        // 1. Try Unified Battery (0x1004) - function 1 (get_status)
        byte? idx1004 = await ResolveFeatureIndexAsync(session, devIdx.Value, FeatureUnifiedBattery, log, ct);
        if (idx1004 is > 0)
        {
            var rx = await TransactHidppAsync(session, devIdx.Value, idx1004.Value, 0x01, [], "HID++ Unified Battery (0x1004)", log, ct);
            if (rx is not null)
                return ParseUnifiedBatteryResponse(rx);
        }

        // 2. Try Battery Status (0x1000) - function 0 (GetBatteryLevelStatus)
        byte? idx1000 = await ResolveFeatureIndexAsync(session, devIdx.Value, FeatureBatteryStatus, log, ct);
        if (idx1000 is > 0)
        {
            var rx = await TransactHidppAsync(session, devIdx.Value, idx1000.Value, 0x00, [], "HID++ Battery Status (0x1000)", log, ct);
            if (rx is not null)
                return ParseLegacyBatteryStatusResponse(rx);
        }

        // 3. Try Battery Voltage (0x1001) - function 0 (GetBatteryVoltage)
        byte? idx1001 = await ResolveFeatureIndexAsync(session, devIdx.Value, FeatureBatteryVoltage, log, ct);
        if (idx1001 is > 0)
        {
            var rx = await TransactHidppAsync(session, devIdx.Value, idx1001.Value, 0x00, [], "HID++ Battery Voltage (0x1001)", log, ct);
            if (rx is not null)
                return ParseBatteryVoltageResponse(rx);
        }

        return null;
    }

    public async Task<MouseProfile?> ReadConfigAsync(IHidSession session, Action<HidPacketLogEntry>? log = null, CancellationToken ct = default)
    {
        var devIdx = await ResolveDeviceIndexAsync(session, log, ct);
        if (!devIdx.HasValue) return null;

        byte? dpiFeature = await ResolveFeatureIndexAsync(session, devIdx.Value, FeatureAdjustableDpi, log, ct);
        if (dpiFeature is not > 0) return null;

        // Feature 0x2201 function 2 = getSensorDpi(sensorIdx=0) -> [sensorIdx, dpiHi, dpiLo, defaultHi, defaultLo]
        var rx = await TransactHidppAsync(session, devIdx.Value, dpiFeature.Value, 0x02, [0x00], "HID++ Read DPI (0x2201)", log, ct);
        if (rx is null || rx.Length < 7) return null;

        int currentDpi = (rx[5] << 8) | rx[6];
        if (currentDpi < 100 || currentDpi > 32000) return null;

        return new MouseProfile
        {
            Id = "device-read",
            Name = "Device (Logitech)",
            IconKey = "Mouse",
            ActiveDpiStageIndex = 0,
            DpiStages = [new DpiStage { Dpi = currentDpi, DpiY = currentDpi, ColorHex = "#FF4CC38A" }],
            PollingRateHz = 1000,
            LodMm = 1.0,
            DebounceMs = 2
        };
    }

    public Task<bool> ApplyProfileAsync(
        IHidSession session,
        MouseProfile profile,
        Action<HidPacketLogEntry>? log = null,
        CancellationToken ct = default,
        MouseProfile? previous = null)
    {
        // Strictly non-intrusive battery/telemetry mode for Logitech to guarantee 0 conflict with G HUB
        return Task.FromResult(false);
    }

    private async Task<byte?> ResolveDeviceIndexAsync(IHidSession session, Action<HidPacketLogEntry>? log, CancellationToken ct)
    {
        byte[] candidates = _cachedDeviceIndex.HasValue
            ? [_cachedDeviceIndex.Value, 0xFF, 0x01, 0x02, 0x03]
            : [0xFF, 0x01, 0x02, 0x03];

        foreach (byte idx in candidates.Distinct())
        {
            // IRoot (feature 0x00) function 1 = ping(0, 0, 0xAA)
            var rx = await TransactHidppAsync(session, idx, 0x00, 0x01, [0x00, 0x00, 0xAA], $"HID++ Ping idx=0x{idx:X2}", log, ct, timeoutMs: 120);
            if (rx is not null && rx.Length >= 7 && rx[2] == 0x00 && rx[6] == 0xAA)
            {
                if (_cachedDeviceIndex != idx)
                {
                    _cachedDeviceIndex = idx;
                    _featureIndexCache.Clear();
                }
                return idx;
            }
        }

        return null;
    }

    private async Task<byte?> ResolveFeatureIndexAsync(IHidSession session, byte devIdx, ushort featureId, Action<HidPacketLogEntry>? log, CancellationToken ct)
    {
        if (_featureIndexCache.TryGetValue(featureId, out byte cached))
            return cached == 0 ? null : cached;

        byte hi = (byte)(featureId >> 8);
        byte lo = (byte)(featureId & 0xFF);
        var rx = await TransactHidppAsync(session, devIdx, 0x00, 0x00, [hi, lo, 0x00], $"HID++ GetFeature 0x{featureId:X4}", log, ct);
        if (rx is null || rx.Length < 5)
            return null;

        byte featIdx = rx[4];
        _featureIndexCache[featureId] = featIdx;
        return featIdx == 0 ? null : featIdx;
    }

    private async Task<byte[]?> TransactHidppAsync(
        IHidSession session,
        byte deviceIndex,
        byte featureIndex,
        byte functionIndex,
        byte[] parameters,
        string label,
        Action<HidPacketLogEntry>? log,
        CancellationToken ct,
        int timeoutMs = 200)
    {
        bool useLong = session.Device.OutputReportLength >= LongReportLength;
        byte reportId = useLong ? LongReportId : ShortReportId;
        int reportLen = useLong ? LongReportLength : ShortReportLength;

        var tx = BuildRequest(reportId, deviceIndex, featureIndex, functionIndex, parameters);
        session.FlushInputQueue();

        log?.Invoke(new HidPacketLogEntry(DateTimeOffset.UtcNow, PacketDirection.Tx, "HID++", HidPacketHelper.ToHexString(tx), label));

        bool written = await session.WriteOutputReportAsync(tx, timeoutMs, ct);
        if (!written)
            return null;

        int inLen = Math.Max(reportLen, session.Device.InputReportLength);
        var rx = await session.ReadMatchingInputReportAsync(
            inLen,
            pkt =>
            {
                if (pkt.Length < 5) return false;
                if (pkt[0] != ShortReportId && pkt[0] != LongReportId) return false;
                // Ignore HID++ 1.0/2.0 error frames from unrelated apps; match our SwId
                if (pkt[2] == 0xFF || pkt[2] == 0x8F) return pkt[1] == deviceIndex;
                return pkt[1] == deviceIndex && (pkt[3] & 0x0F) == SoftwareId;
            },
            timeoutMs,
            maxAttempts: 5,
            ct);

        if (rx is null)
            return null;

        log?.Invoke(new HidPacketLogEntry(DateTimeOffset.UtcNow, PacketDirection.Rx, "HID++", HidPacketHelper.ToHexString(rx), label));

        // HID++ error reply (0xFF or 0x8F) means device asleep or feature unsupported
        if (rx[2] == 0xFF || rx[2] == 0x8F)
            return null;

        return rx;
    }
}
