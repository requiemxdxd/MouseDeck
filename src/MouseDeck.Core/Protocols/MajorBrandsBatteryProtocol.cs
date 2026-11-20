using MouseDeck.Core.Models;
using MouseDeck.Core.Transport;

namespace MouseDeck.Core.Protocols;

/// <summary>
/// Non-intrusive read-only battery telemetry protocol for major gaming brands:
///   - SteelSeries Wireless (VID 0x1038: Aerox 3/5/9, Prime Wireless, Rival 3/650)
///   - Corsair Slipstream (VID 0x1B1C: Dark Core, Harpoon, Sabre, M75, Katar)
///   - HyperX Pulsefire Wireless (VID 0x03F0 / 0x0951: Pulsefire Haste 2 / Dart)
/// </summary>
public sealed class MajorBrandsBatteryProtocol : IMouseProtocol
{
    public const ushort SteelSeriesVid = 0x1038;
    public const ushort CorsairVid = 0x1B1C;
    public const ushort HyperXHpVid = 0x03F0;
    public const ushort HyperXKingstonVid = 0x0951;

    public string ProtocolId => "major-brands-battery";
    public string DisplayName => "SteelSeries / Corsair / HyperX";
    public int Priority => 75;

    public MouseCapabilities Capabilities { get; } = MouseCapabilities.BatteryOnlyWireless;

    public IReadOnlyList<DeviceMatchRule> SupportedDevices { get; } =
    [
        new(SteelSeriesVid, null, null, FriendlyModelName: "SteelSeries Wireless Mouse"),
        new(CorsairVid, null, null, FriendlyModelName: "Corsair Wireless Mouse"),
        new(HyperXHpVid, null, null, FriendlyModelName: "HyperX Wireless Mouse"),
        new(HyperXKingstonVid, null, null, FriendlyModelName: "HyperX Wireless Mouse")
    ];

    public int MatchScore(MouseDeviceInfo device)
    {
        if (device.IsVirtual || device.IsBluetooth || !device.CanOpenReadWrite || !device.IsVendorControlInterface)
            return 0;

        bool knownVid = device.VendorId is SteelSeriesVid or CorsairVid or HyperXHpVid or HyperXKingstonVid;
        if (!knownVid) return 0;

        // Avoid grabbing headsets/keyboards from the same brand when product name clearly indicates non-mouse
        var name = device.ProductName.ToLowerInvariant();
        if (name.Contains("keyboard") || name.Contains("headset") || name.Contains("arctis") || name.Contains("cloud") || name.Contains("virtuoso") || name.Contains("void"))
            return 0;

        int score = 55;
        if (name.Contains("mouse") || name.Contains("aerox") || name.Contains("rival") || name.Contains("prime") ||
            name.Contains("pulsefire") || name.Contains("haste") || name.Contains("dart") ||
            name.Contains("dark core") || name.Contains("sabre") || name.Contains("harpoon") || name.Contains("m75") || name.Contains("katar"))
            score += 30;

        if (device.OutputReportLength >= 32 || device.FeatureReportLength >= 32)
            score += 10;

        return score;
    }

    public MouseTransportKind DetectTransport(MouseDeviceInfo device)
    {
        var name = device.ProductName.ToLowerInvariant();
        if (name.Contains("wired")) return MouseTransportKind.WiredUsb;
        return MouseTransportKind.Wireless24G;
    }

    public async Task<bool> PingWakeStateAsync(IHidSession session, Action<HidPacketLogEntry>? log = null, CancellationToken ct = default)
    {
        var r = await ReadBatteryAsync(session, log, ct);
        return r is { IsMouseAwake: true };
    }

    public async Task<BatteryReading?> ReadBatteryAsync(IHidSession session, Action<HidPacketLogEntry>? log = null, CancellationToken ct = default)
    {
        return session.Device.VendorId switch
        {
            SteelSeriesVid => await ReadSteelSeriesBatteryAsync(session, log, ct),
            CorsairVid => await ReadCorsairBatteryAsync(session, log, ct),
            HyperXHpVid or HyperXKingstonVid => await ReadHyperXBatteryAsync(session, log, ct),
            _ => null
        };
    }

    public static BatteryReading? ParseSteelSeriesResponse(ReadOnlySpan<byte> rx)
    {
        if (rx.Length < 4) return null;
        int off = rx[0] == 0x00 ? 1 : 0;
        if (rx[off] != 0xD2 && rx[off] != 0x92 && rx[off] != 0xAA) return null;

        int raw = rx[off + 1];
        bool charging = (raw & 0x80) != 0 || (rx.Length > off + 2 && rx[off + 2] == 0x01);
        int level = raw & 0x7F;
        if (level == 0 && !charging)
            return new BatteryReading(0, false, null, IsMouseAwake: false);

        // Some SteelSeries models report 0..100 directly, older Aerox reports 1..20 (5% steps)
        int pct = level <= 20 ? Math.Clamp(level * 5, 1, 100) : Math.Clamp(level, 1, 100);
        return new BatteryReading(pct, charging, null, IsMouseAwake: true);
    }

    public static BatteryReading? ParseCorsairResponse(ReadOnlySpan<byte> rx)
    {
        if (rx.Length < 6) return null;
        int off = rx[0] == 0x00 ? 1 : 0;
        // Corsair Slipstream battery query response (0x08 0x02 0x0F -> level in 0..1000 tenths of %)
        if (rx[off] == 0x08 || rx[off + 1] == 0x02)
        {
            int tenths = rx[off + 3] | (rx[off + 4] << 8);
            if (tenths is > 0 and <= 1000)
                return new BatteryReading(Math.Clamp((int)Math.Round(tenths / 10.0, MidpointRounding.AwayFromZero), 1, 100), false, null, IsMouseAwake: true);
        }
        return null;
    }

    public static BatteryReading? ParseHyperXResponse(ReadOnlySpan<byte> rx)
    {
        if (rx.Length < 6) return null;
        int off = rx[0] == 0x00 ? 1 : 0;
        if (rx[off] == 0x51 || rx[off] == 0x04)
        {
            int pct = rx[off + 3];
            bool charging = rx[off + 4] != 0;
            if (pct == 0 && !charging)
                return new BatteryReading(0, false, null, IsMouseAwake: false);
            if (pct is >= 1 and <= 100)
                return new BatteryReading(pct, charging, null, IsMouseAwake: true);
        }
        return null;
    }

    private static async Task<BatteryReading?> ReadSteelSeriesBatteryAsync(IHidSession session, Action<HidPacketLogEntry>? log, CancellationToken ct)
    {
        int len = Math.Max(64, Math.Max(session.Device.OutputReportLength, session.Device.FeatureReportLength));
        foreach (byte cmd in new byte[] { 0xD2, 0x92 })
        {
            var tx = new byte[len];
            tx[0] = 0x00;
            tx[1] = cmd;
            session.FlushInputQueue();
            log?.Invoke(new HidPacketLogEntry(DateTimeOffset.UtcNow, PacketDirection.Tx, "SteelSeries", HidPacketHelper.ToHexString(tx.AsSpan(0, 8)), "Read Battery"));
            if (!await session.WriteOutputReportAsync(tx, 200, ct)) continue;

            var rx = await session.ReadMatchingInputReportAsync(
                Math.Max(len, session.Device.InputReportLength),
                p => p.Length >= 4 && (p[0] == cmd || p[1] == cmd),
                200, 3, ct);

            if (rx is not null)
            {
                log?.Invoke(new HidPacketLogEntry(DateTimeOffset.UtcNow, PacketDirection.Rx, "SteelSeries", HidPacketHelper.ToHexString(rx.AsSpan(0, Math.Min(8, rx.Length))), "Battery RX"));
                var parsed = ParseSteelSeriesResponse(rx);
                if (parsed is not null) return parsed;
            }
        }
        return null;
    }

    private static async Task<BatteryReading?> ReadCorsairBatteryAsync(IHidSession session, Action<HidPacketLogEntry>? log, CancellationToken ct)
    {
        int len = Math.Max(64, session.Device.OutputReportLength);
        var tx = new byte[len];
        tx[0] = 0x00;
        tx[1] = 0x08;
        tx[2] = 0x02;
        tx[3] = 0x0F; // Battery level property
        session.FlushInputQueue();
        log?.Invoke(new HidPacketLogEntry(DateTimeOffset.UtcNow, PacketDirection.Tx, "Corsair", HidPacketHelper.ToHexString(tx.AsSpan(0, 8)), "Read Battery"));
        if (!await session.WriteOutputReportAsync(tx, 200, ct)) return null;

        var rx = await session.ReadMatchingInputReportAsync(
            Math.Max(len, session.Device.InputReportLength),
            p => p.Length >= 6 && (p[0] == 0x08 || p[1] == 0x08),
            200, 3, ct);

        if (rx is not null)
        {
            log?.Invoke(new HidPacketLogEntry(DateTimeOffset.UtcNow, PacketDirection.Rx, "Corsair", HidPacketHelper.ToHexString(rx.AsSpan(0, Math.Min(8, rx.Length))), "Battery RX"));
            return ParseCorsairResponse(rx);
        }
        return null;
    }

    private static async Task<BatteryReading?> ReadHyperXBatteryAsync(IHidSession session, Action<HidPacketLogEntry>? log, CancellationToken ct)
    {
        int len = Math.Max(64, session.Device.OutputReportLength);
        var tx = new byte[len];
        tx[0] = 0x00;
        tx[1] = 0x51; // HyperX battery query
        session.FlushInputQueue();
        log?.Invoke(new HidPacketLogEntry(DateTimeOffset.UtcNow, PacketDirection.Tx, "HyperX", HidPacketHelper.ToHexString(tx.AsSpan(0, 8)), "Read Battery"));
        if (!await session.WriteOutputReportAsync(tx, 200, ct)) return null;

        var rx = await session.ReadMatchingInputReportAsync(
            Math.Max(len, session.Device.InputReportLength),
            p => p.Length >= 6 && (p[0] == 0x51 || p[1] == 0x51),
            200, 3, ct);

        if (rx is not null)
        {
            log?.Invoke(new HidPacketLogEntry(DateTimeOffset.UtcNow, PacketDirection.Rx, "HyperX", HidPacketHelper.ToHexString(rx.AsSpan(0, Math.Min(8, rx.Length))), "Battery RX"));
            return ParseHyperXResponse(rx);
        }
        return null;
    }

    public Task<MouseProfile?> ReadConfigAsync(IHidSession session, Action<HidPacketLogEntry>? log = null, CancellationToken ct = default)
        => Task.FromResult<MouseProfile?>(null);

    public Task<bool> ApplyProfileAsync(IHidSession session, MouseProfile profile, Action<HidPacketLogEntry>? log = null, CancellationToken ct = default, MouseProfile? previous = null)
        => Task.FromResult(false);
}
