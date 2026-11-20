using MouseDeck.Core.Models;
using MouseDeck.Core.Transport;

namespace MouseDeck.Core.Protocols;

/// <summary>
/// Driver for Nordic nRF52840 / 4K-8K wireless gaming mice (PAW3395 / PAW3950).
/// Covers Nordic-based VXE/ATK 4K-8K dongles, Scyrox V8, WLmouse, Lamzu 4K, Darmoshark 4K.
/// Uses 64-byte (or 32-byte) vendor HID interrupt packets on UsagePage 0xFF00 / 0xFF02 / 0xFFFF.
/// </summary>
public sealed class NordicHidProtocol : IMouseProtocol
{
    public const byte DefaultReportId = 0x06;
    private const byte OpPing = 0x01;
    private const byte OpBattery = 0x02;
    private const byte OpReadConfig = 0x03;
    private const byte OpPollingRate = 0x10;
    private const byte OpDpiConfig = 0x11;
    private const byte OpSensorConfig = 0x12;
    private const byte OpPowerConfig = 0x13;

    public string ProtocolId => "nordic-nrf52840";
    public string DisplayName => "Nordic nRF52840 (4K/8K PAW3950)";
    public int Priority => 90;

    public MouseCapabilities Capabilities => MouseCapabilities.Paw3950HighRate;

    public IReadOnlyList<DeviceMatchRule> SupportedDevices { get; } =
    [
        new(0x1915, null, 0xFF00, FriendlyModelName: "Nordic nRF52840 Mouse"),
        new(0x36A7, null, 0xFF00, FriendlyModelName: "Scyrox PAW3950 8K"),
        new(0x3710, null, 0xFF00, FriendlyModelName: "WLmouse PAW3950 8K"),
        new(0x373B, null, 0xFF00, FriendlyModelName: "ATK / VXE 8K Dongle"),
        new(0x3554, null, 0xFF00, FriendlyModelName: "VGN / VXE 4K-8K Dongle"),
        new(0x25A7, null, 0xFF00, FriendlyModelName: "Darmoshark 4K Wireless")
    ];

    public int MatchScore(MouseDeviceInfo device)
    {
        if (device.IsVirtual || !device.CanOpenReadWrite)
            return 0;

        int best = 0;
        foreach (var rule in SupportedDevices)
        {
            if (device.VendorId != rule.VendorId) continue;
            if (rule.ProductId.HasValue && device.ProductId != rule.ProductId.Value) continue;
            if (!device.IsVendorControlInterface) continue;

            // Distinguish 64-byte Nordic 4K/8K dongles from 17-byte CompX interfaces
            if (device.OutputReportLength < 32 && device.FeatureReportLength < 32)
                continue;

            int score = 60;
            if (device.UsagePage == 0xFF00 || device.UsagePage == 0xFFFF) score += 20;
            if (device.OutputReportLength >= 64) score += 25;
            if (rule.ProductId.HasValue) score += 20;

            best = Math.Max(best, score);
        }

        return best;
    }

    public MouseTransportKind DetectTransport(MouseDeviceInfo device)
    {
        var name = device.ProductName.ToLowerInvariant();
        if (name.Contains("wired") || name.Contains("cable"))
            return MouseTransportKind.WiredUsb;
        return MouseTransportKind.Wireless24G;
    }

    public static byte[] BuildPacket(int length, byte opcode, ReadOnlySpan<byte> payload, byte reportId = DefaultReportId)
    {
        int size = Math.Max(32, length);
        var buf = new byte[size];
        buf[0] = reportId;
        buf[1] = 0xAA; // Frame sync magic
        buf[2] = opcode;
        buf[3] = (byte)Math.Min(payload.Length, size - 5);

        if (!payload.IsEmpty)
        {
            payload[..buf[3]].CopyTo(buf.AsSpan(4));
        }

        // Trailing XOR checksum
        buf[^1] = HidPacketHelper.ComputeXor8(buf.AsSpan(0, size - 1));
        return buf;
    }

    public static BatteryReading? ParseBatteryResponse(ReadOnlySpan<byte> rx)
    {
        if (rx.Length < 10)
            return null;

        int idx = rx[0] == DefaultReportId ? 1 : 0;
        if (rx[idx] != 0xAA && rx[idx] != 0x55)
            return null;

        byte awakeFlag = rx[idx + 3];
        int pct = rx[idx + 4];
        bool charging = rx[idx + 5] != 0;
        int voltage = rx[idx + 6] | (rx[idx + 7] << 8);

        if (awakeFlag == 0 && pct == 0 && !charging)
            return new BatteryReading(0, false, null, IsMouseAwake: false);

        return new BatteryReading(
            Math.Clamp(pct, 1, 100),
            charging,
            voltage is >= 2800 and <= 4500 ? voltage : null,
            IsMouseAwake: awakeFlag != 0 || charging);
    }

    public async Task<bool> PingWakeStateAsync(IHidSession session, Action<HidPacketLogEntry>? log = null, CancellationToken ct = default)
    {
        var tx = BuildPacket(ResolvePacketLen(session.Device), OpPing, []);
        var rx = await TransactAsync(session, tx, "Nordic Ping", log, ct);
        return rx is not null && rx.Length >= 5 && rx[4] != 0;
    }

    public async Task<BatteryReading?> ReadBatteryAsync(IHidSession session, Action<HidPacketLogEntry>? log = null, CancellationToken ct = default)
    {
        var tx = BuildPacket(ResolvePacketLen(session.Device), OpBattery, []);
        var rx = await TransactAsync(session, tx, "Nordic Battery", log, ct);
        return rx is null ? null : ParseBatteryResponse(rx);
    }

    public async Task<MouseProfile?> ReadConfigAsync(IHidSession session, Action<HidPacketLogEntry>? log = null, CancellationToken ct = default)
    {
        var tx = BuildPacket(ResolvePacketLen(session.Device), OpReadConfig, []);
        var rx = await TransactAsync(session, tx, "Nordic Read Config", log, ct);
        if (rx is null || rx.Length < 16)
            return null;

        int idx = rx[0] == DefaultReportId ? 1 : 0;
        if (rx[idx] != 0xAA && rx[idx] != 0x55)
            return null;

        int hz = rx[idx + 3] | (rx[idx + 4] << 8);
        if (!Capabilities.SupportedPollingRatesHz.Contains(hz))
            return null;

        int count = Math.Clamp((int)rx[idx + 5], 1, 6);
        int active = Math.Clamp((int)rx[idx + 6], 0, count - 1);
        double lod = rx[idx + 7] switch { 0 => 0.7, 2 => 2.0, _ => 1.0 };
        int debounce = Math.Clamp((int)rx[idx + 8], 0, 20);
        byte flags = rx[idx + 9];
        int sleepMin = Math.Clamp((int)rx[idx + 10], 1, 30);

        string[] colors = ["#FFEF4444", "#FF38BDF8", "#FF22C55E", "#FFEAB308", "#FFA855F7", "#FFEC4899"];
        var stages = new List<DpiStage>(count);
        for (int i = 0; i < count; i++)
        {
            int pos = idx + 11 + (i * 2);
            int dpi = pos + 1 < rx.Length ? Math.Clamp(rx[pos] | (rx[pos + 1] << 8), Capabilities.MinDpi, Capabilities.MaxDpi) : 800;
            stages.Add(new DpiStage { Dpi = dpi, DpiY = dpi, ColorHex = colors[i % colors.Length] });
        }

        return new MouseProfile
        {
            Id = "device-read",
            Name = "Device (Current)",
            IconKey = "Mouse",
            ActiveDpiStageIndex = active,
            DpiStages = stages,
            PollingRateHz = hz,
            LodMm = lod,
            DebounceMs = debounce,
            MotionSync = (flags & 0x01) != 0,
            RippleControl = (flags & 0x02) != 0,
            AngleSnapping = (flags & 0x04) != 0,
            HighSpeedMode = (flags & 0x08) != 0,
            SleepTimeoutMinutes = sleepMin
        };
    }

    public async Task<bool> ApplyProfileAsync(
        IHidSession session,
        MouseProfile profile,
        Action<HidPacketLogEntry>? log = null,
        CancellationToken ct = default,
        MouseProfile? previous = null)
    {
        int len = ResolvePacketLen(session.Device);
        bool ok = true;

        // 1. Polling Rate (125..8000 Hz)
        ushort hz = (ushort)Math.Clamp(profile.PollingRateHz, 125, 8000);
        ok &= await TransactAsync(session, BuildPacket(len, OpPollingRate, [(byte)(hz & 0xFF), (byte)(hz >> 8)]), $"Nordic Rate {hz} Hz", log, ct) is not null;

        // 2. DPI stages (active index, count, 16-bit LE values)
        int count = Math.Clamp(profile.DpiStages.Count, 1, 6);
        int active = Math.Clamp(profile.ActiveDpiStageIndex, 0, count - 1);
        var dpiPayload = new byte[2 + (count * 2)];
        dpiPayload[0] = (byte)count;
        dpiPayload[1] = (byte)active;
        for (int i = 0; i < count; i++)
        {
            ushort dpi = (ushort)Math.Clamp(profile.DpiStages[i].Dpi, Capabilities.MinDpi, Capabilities.MaxDpi);
            dpiPayload[2 + (i * 2)] = (byte)(dpi & 0xFF);
            dpiPayload[3 + (i * 2)] = (byte)(dpi >> 8);
        }
        ok &= await TransactAsync(session, BuildPacket(len, OpDpiConfig, dpiPayload), $"Nordic DPI #{active + 1} ({profile.CurrentDpi})", log, ct) is not null;

        // 3. Sensor config: LOD (0=0.7mm, 1=1.0mm, 2=2.0mm), Debounce, bitmask flags, AngleTuning (sbyte)
        byte lod = profile.LodMm <= 0.85 ? (byte)0 : profile.LodMm >= 1.5 ? (byte)2 : (byte)1;
        byte debounce = (byte)Math.Clamp(profile.DebounceMs, 0, 20);
        byte flags = 0;
        if (profile.MotionSync) flags |= 0x01;
        if (profile.RippleControl) flags |= 0x02;
        if (profile.AngleSnapping) flags |= 0x04;
        if (profile.HighSpeedMode) flags |= 0x08;
        byte angleByte = unchecked((byte)(sbyte)Math.Clamp(profile.AngleTuningDegrees, -15, 15));
        ok &= await TransactAsync(session, BuildPacket(len, OpSensorConfig, [lod, debounce, flags, angleByte]), $"Nordic Sensor LOD={profile.LodMm:0.0} Deb={debounce}ms Angle={profile.AngleTuningDegrees}°", log, ct) is not null;

        // 4. Power/Sleep timeout in minutes
        byte sleepMin = (byte)Math.Clamp(profile.SleepTimeoutMinutes, 1, 60);
        ok &= await TransactAsync(session, BuildPacket(len, OpPowerConfig, [sleepMin]), $"Nordic Sleep {sleepMin}m", log, ct) is not null;

        return ok;
    }

    private static int ResolvePacketLen(MouseDeviceInfo dev)
    {
        if (dev.OutputReportLength >= 32) return dev.OutputReportLength;
        if (dev.FeatureReportLength >= 32) return dev.FeatureReportLength;
        return 64;
    }

    private async Task<byte[]?> TransactAsync(
        IHidSession session,
        byte[] tx,
        string label,
        Action<HidPacketLogEntry>? log,
        CancellationToken ct)
    {
        bool useInterrupt = session.Device.OutputReportLength >= 32;
        string ch = useInterrupt ? "Interrupt" : "Feature";
        log?.Invoke(new HidPacketLogEntry(DateTimeOffset.UtcNow, PacketDirection.Tx, ch, HidPacketHelper.ToHexString(tx), label));

        if (useInterrupt)
        {
            session.FlushInputQueue();
            if (!await session.WriteOutputReportAsync(tx, 250, ct))
            {
                log?.Invoke(new HidPacketLogEntry(DateTimeOffset.UtcNow, PacketDirection.Error, ch, string.Empty, $"{label}: WriteOutput failed"));
                return null;
            }

            int inLen = Math.Max(32, session.Device.InputReportLength);
            var rx = await session.ReadMatchingInputReportAsync(
                inLen,
                pkt => pkt.Length >= 4 && (pkt[0] == tx[0] || pkt[0] == 0xAA || pkt[0] == 0x55 || pkt[1] == 0xAA || pkt[1] == 0x55),
                timeoutMs: 250,
                maxAttempts: 4,
                ct);
            if (rx is not null)
                log?.Invoke(new HidPacketLogEntry(DateTimeOffset.UtcNow, PacketDirection.Rx, ch, HidPacketHelper.ToHexString(rx), label));
            return rx;
        }
        else
        {
            if (!await session.SetFeatureReportAsync(tx, ct))
            {
                log?.Invoke(new HidPacketLogEntry(DateTimeOffset.UtcNow, PacketDirection.Error, ch, string.Empty, $"{label}: SetFeature failed"));
                return null;
            }

            await Task.Delay(15, ct);
            var rx = await session.GetFeatureReportAsync(tx[0], tx.Length, ct);
            if (rx is not null)
                log?.Invoke(new HidPacketLogEntry(DateTimeOffset.UtcNow, PacketDirection.Rx, ch, HidPacketHelper.ToHexString(rx), label));
            return rx;
        }
    }
}
