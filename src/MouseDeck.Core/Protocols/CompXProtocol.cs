using MouseDeck.Core.Models;
using MouseDeck.Core.Transport;

namespace MouseDeck.Core.Protocols;

/// <summary>
/// Driver for CompX CX52850 / CX52650 wireless gaming mice (PAW3395 / PAW3950).
/// Covers Attack Shark X3/X6/R1, Kysona M600, Ajazz AJ199/AJ139, Darmoshark M3/N3,
/// Zaopin Z1 Pro, Delux M800 Pro, and CompX-based VXE/VGN/MCHOSE dongles.
/// Uses 17-byte HID Report ID 0x08 with checksum byte[16] = (0x55 - sum(0..15)) &amp; 0xFF.
/// </summary>
public sealed class CompXProtocol : IMouseProtocol
{
    public const byte ReportId = 0x08;
    public const int PacketLength = 17;

    private const byte CmdOnlineStatus = 0x03;
    private const byte CmdBattery = 0x04;
    private const byte CmdWriteEeprom = 0x07;
    private const byte CmdReadEeprom = 0x08;

    public string ProtocolId => "compx-cx52850";
    public string DisplayName => "CompX CX52850 (8K)";
    public int Priority => 80;

    public MouseCapabilities Capabilities { get; } = new()
    {
        MinDpi = 50,
        MaxDpi = 30000,
        DpiStep = 50,
        MaxDpiStages = 6,
        SupportedPollingRatesHz = [125, 250, 500, 1000, 2000, 4000, 8000],
        SupportedLodMm = [0.7, 1.0, 2.0],
        MinDebounceMs = 0,
        MaxDebounceMs = 20,
        SupportsMotionSync = true,
        SupportsRippleControl = true,
        SupportsAngleSnapping = true,
        SupportsHighSpeedMode = true,
        SupportsBatteryRead = true,
        IsWriteOnlyConfig = true
    };

    public IReadOnlyList<DeviceMatchRule> SupportedDevices { get; } =
    [
        // VXE MAD R / ATK MAD 8K Dongle (VID 0x373B)
        new(0x373B, 0x1040, 0xFF02, FriendlyModelName: "VXE MAD 8K Dongle"),
        new(0x373B, null, 0xFF02, FriendlyModelName: "ATK / VXE 8K Mouse"),

        // Attack Shark X3 / Kysona M600 / Ajazz / Zaopin (CompX VID 0x1D57)
        new(0x1D57, 0xFA60, 0xFF02, FriendlyModelName: "CompX PAW3395 Mouse (2.4G)"),
        new(0x1D57, 0xFA61, 0xFF02, FriendlyModelName: "CompX PAW3395 Mouse (Wired)", TransportHint: MouseTransportKind.WiredUsb),
        new(0x1D57, null, 0xFF02, FriendlyModelName: "CompX Wireless Mouse"),
        new(0x1D57, null, 0xFF00, FriendlyModelName: "CompX Wireless Mouse"),

        // VGN / VXE / ATK / MCHOSE CompX (VID 0x3554)
        new(0x3554, 0xF58E, 0xFF02, FriendlyModelName: "VXE / VGN PAW3395 (2.4G)"),
        new(0x3554, 0xF58F, 0xFF02, FriendlyModelName: "VXE / VGN PAW3395 (Wired)", TransportHint: MouseTransportKind.WiredUsb),
        new(0x3554, 0xF5D5, 0xFF02, FriendlyModelName: "VXE R1 Series (2.4G)"),
        new(0x3554, null, 0xFF02, FriendlyModelName: "Compx / VXE Gaming Mouse"),
        new(0x3554, null, 0xFF00, FriendlyModelName: "Compx / VXE Gaming Mouse"),

        // Telink / CompX / Darmoshark / Delux / MCHOSE / Incott (VID 0x248A, 0x1EA7, 0x25A7)
        new(0x248A, null, 0xFF02, FriendlyModelName: "CompX 2.4G Gaming Mouse"),
        new(0x1EA7, null, 0xFF02, FriendlyModelName: "CompX PAW3395 Mouse"),
        new(0x25A7, null, 0xFF02, FriendlyModelName: "MCHOSE / Darmoshark Mouse")
    ];

    public int MatchScore(MouseDeviceInfo device)
    {
        if (device.IsVirtual || device.IsBluetooth || !device.CanOpenReadWrite)
            return 0;

        int best = 0;
        foreach (var rule in SupportedDevices)
        {
            if (device.VendorId != rule.VendorId) continue;
            if (rule.ProductId.HasValue && device.ProductId != rule.ProductId.Value) continue;
            if (rule.UsagePage.HasValue && device.UsagePage != rule.UsagePage.Value) continue;

            int score = 40;
            if (rule.ProductId.HasValue) score += 30;
            if (device.IsVendorControlInterface) score += 20;
            if (device.FeatureReportLength == PacketLength || device.OutputReportLength == PacketLength)
                score += 25;
            else if (device.FeatureReportLength == 65 || device.OutputReportLength == 65)
                score += 18;
            if (device.UsagePage == 0xFF02)
                score += 10;

            best = Math.Max(best, score);
        }
        return best;
    }

    public MouseTransportKind DetectTransport(MouseDeviceInfo device)
    {
        var name = device.ProductName.ToLowerInvariant();
        if (name.Contains("wired") || name.Contains("usb"))
            return MouseTransportKind.WiredUsb;

        var rule = SupportedDevices.FirstOrDefault(r =>
            r.VendorId == device.VendorId && r.ProductId.HasValue && r.ProductId.Value == device.ProductId);

        return rule?.TransportHint ?? MouseTransportKind.Wireless24G;
    }

    public static byte[] BuildPacket(byte command, ushort address, ReadOnlySpan<byte> payload)
    {
        var packet = new byte[PacketLength];
        packet[0] = ReportId;
        packet[1] = command;
        packet[2] = 0x00;
        packet[3] = (byte)((address >> 8) & 0xFF);
        packet[4] = (byte)(address & 0xFF);
        packet[5] = (byte)Math.Min(payload.Length, 10);

        if (!payload.IsEmpty)
        {
            payload[..packet[5]].CopyTo(packet.AsSpan(6));
        }

        // CompX checksum over bytes 0..15
        packet[16] = HidPacketHelper.ComputeCompX55(packet.AsSpan(0, 16));
        return packet;
    }

    public static BatteryReading? ParseBatteryResponse(ReadOnlySpan<byte> rx)
    {
        if (rx.Length < 10)
            return null;

        // Accept either exact ReportId 0x08 header or shifted payload when OS strips/keeps Report ID
        int offset = rx[0] == ReportId ? 0 : -1;
        byte cmd = offset == 0 ? rx[1] : rx[0];
        if (cmd != CmdBattery && cmd != CmdOnlineStatus)
            return null;

        int pctIdx = offset == 0 ? 6 : 5;
        int chgIdx = offset == 0 ? 7 : 6;
        int vHiIdx = offset == 0 ? 8 : 7;
        int vLoIdx = offset == 0 ? 9 : 8;

        int rawPercent = rx[pctIdx];
        bool isCharging = rx[chgIdx] != 0;
        int rawVoltage = (rx[vHiIdx] << 8) | rx[vLoIdx];

        // CompX dongles return 0% and 0 mV when the mouse is in deep sleep / unreachable over 2.4G RF
        if (rawPercent == 0 && rawVoltage == 0 && !isCharging)
        {
            return new BatteryReading(0, false, null, IsMouseAwake: false);
        }

        int clampedPercent = Math.Clamp(rawPercent, 1, 100);
        int? voltageMv = rawVoltage is >= 2800 and <= 4500 ? rawVoltage : null;
        return new BatteryReading(clampedPercent, isCharging, voltageMv, IsMouseAwake: true);
    }

    public async Task<bool> PingWakeStateAsync(IHidSession session, Action<HidPacketLogEntry>? log = null, CancellationToken ct = default)
    {
        var tx = BuildPacket(CmdOnlineStatus, 0x0000, []);
        var rx = await TransactAsync(session, tx, "Ping Online/Sleep", log, ct);
        if (rx is null || rx.Length < 7)
            return false;

        // Byte 5 or 6 non-zero indicates wireless link active
        return rx[5] != 0 || rx[6] != 0;
    }

    public async Task<BatteryReading?> ReadBatteryAsync(IHidSession session, Action<HidPacketLogEntry>? log = null, CancellationToken ct = default)
    {
        var tx = BuildPacket(CmdBattery, 0x0000, []);
        var rx = await TransactAsync(session, tx, "Read Battery", log, ct);
        if (rx is null)
            return null;

        return ParseBatteryResponse(rx);
    }

    public async Task<MouseProfile?> ReadConfigAsync(IHidSession session, Action<HidPacketLogEntry>? log = null, CancellationToken ct = default)
    {
        // Read base config block (addr 0x0000, 10 bytes: polling rate, DPI count, active stage, LOD)
        var baseRx = await TransactAsync(session, BuildReadPacket(0x0000, 10), "Read Config Base", log, ct);
        if (baseRx is null || baseRx.Length < 16 || baseRx[1] != CmdReadEeprom)
            return null;

        byte rateCode = baseRx[6];
        int pollingRate = DecodePollingRate(rateCode);
        if (pollingRate == 0)
            return null; // Unrecognized/empty EEPROM reply (write-only firmware)

        int stageCount = Math.Clamp((int)baseRx[8], 1, 6);
        int activeIdx = Math.Clamp((int)baseRx[10], 0, stageCount - 1);

        var stages = new List<DpiStage>(stageCount);
        string[] defaultColors = ["#FFEF4444", "#FF38BDF8", "#FF22C55E", "#FFEAB308", "#FFA855F7", "#FFEC4899"];
        bool independentXY = false;

        for (int i = 0; i < stageCount; i++)
        {
            ushort addr = (ushort)(0x000C + (i * 4));
            var stRx = await TransactAsync(session, BuildReadPacket(addr, 4), $"Read DPI #{i + 1}", log, ct);
            if (stRx is not null && stRx.Length >= 10 && stRx[1] == CmdReadEeprom)
            {
                byte loX = stRx[6];
                byte loY = stRx[7];
                byte hiBoth = stRx[8];
                int encX = loX | ((hiBoth & 0x03) << 8);
                int encY = loY | (((hiBoth >> 2) & 0x03) << 8);
                int dpiX = Math.Clamp((encX + 1) * 50, Capabilities.MinDpi, Capabilities.MaxDpi);
                int dpiY = Math.Clamp((encY + 1) * 50, Capabilities.MinDpi, Capabilities.MaxDpi);
                if (dpiX != dpiY) independentXY = true;
                stages.Add(new DpiStage { Dpi = dpiX, DpiY = dpiY, ColorHex = defaultColors[i % defaultColors.Length] });
            }
            else
            {
                stages.Add(new DpiStage { Dpi = 800 * (i + 1), DpiY = 800 * (i + 1), ColorHex = defaultColors[i % defaultColors.Length] });
            }
        }

        // Read LOD (0x000A): 0=0.7mm (PAW3950), 1=1.0mm, 2=2.0mm
        double lodMm = 1.0;
        var lodRx = await TransactAsync(session, BuildReadPacket(0x000A, 2), "Read LOD", log, ct);
        if (lodRx is not null && lodRx.Length >= 8 && lodRx[1] == CmdReadEeprom)
            lodMm = lodRx[6] == 0 ? 0.7 : lodRx[6] >= 2 ? 2.0 : 1.0;

        int debounceMs = 2;
        bool motionSync = false, ripple = false, angleSnap = false;
        int sleepMin = 5;
        var sensorRx = await TransactAsync(session, BuildReadPacket(0x00A9, 10), "Read Sensor Flags", log, ct);
        if (sensorRx is not null && sensorRx.Length >= 16 && sensorRx[1] == CmdReadEeprom)
        {
            debounceMs = Math.Clamp((int)sensorRx[6], 0, 20);
            motionSync = sensorRx[8] != 0;
            sleepMin = Math.Clamp((int)Math.Round(sensorRx[10] / 6.0), 1, 30);
            ripple = sensorRx[12] != 0;
            angleSnap = sensorRx[14] != 0;
        }

        return new MouseProfile
        {
            Id = "device-read",
            Name = "Device (Current)",
            IconKey = "Mouse",
            ActiveDpiStageIndex = activeIdx,
            IndependentXY = independentXY,
            DpiStages = stages,
            PollingRateHz = pollingRate,
            LodMm = lodMm,
            DebounceMs = debounceMs,
            MotionSync = motionSync,
            RippleControl = ripple,
            AngleSnapping = angleSnap,
            SleepTimeoutMinutes = sleepMin
        };
    }

    public static byte[] BuildReadPacket(ushort address, byte readLength)
    {
        var packet = new byte[PacketLength];
        packet[0] = ReportId;
        packet[1] = CmdReadEeprom;
        packet[2] = 0x00;
        packet[3] = (byte)((address >> 8) & 0xFF);
        packet[4] = (byte)(address & 0xFF);
        packet[5] = Math.Clamp(readLength, (byte)1, (byte)10);
        packet[16] = HidPacketHelper.ComputeCompX55(packet.AsSpan(0, 16));
        return packet;
    }

    public static int DecodePollingRate(byte code) => code switch
    {
        0x08 => 125,
        0x04 => 250,
        0x02 => 500,
        0x01 => 1000,
        0x10 => 2000,
        0x20 => 4000,
        0x40 => 8000,
        _ => 0
    };

    public async Task<bool> ApplyProfileAsync(
        IHidSession session,
        MouseProfile profile,
        Action<HidPacketLogEntry>? log = null,
        CancellationToken ct = default,
        MouseProfile? previous = null)
    {
        bool allOk = true;
        bool fullSync = previous is null;

        // 1. Polling Rate (addr 0x0000)
        if (fullSync || previous!.PollingRateHz != profile.PollingRateHz)
        {
            byte rateCode = EncodePollingRate(profile.PollingRateHz);
            byte rateInv = (byte)(0x55 - rateCode);
            allOk &= await SendWriteCommandAsync(session, 0x0000, [rateCode, rateInv], $"PollingRate {profile.PollingRateHz} Hz", log, ct);
        }

        // 2. Active DPI stage + count (addr 0x0002 & 0x0004)
        byte stageCount = (byte)Math.Clamp(profile.DpiStages.Count, 1, 6);
        byte activeIdx = (byte)Math.Clamp(profile.ActiveDpiStageIndex, 0, stageCount - 1);
        if (fullSync || previous!.DpiStages.Count != stageCount || previous.ActiveDpiStageIndex != activeIdx)
        {
            allOk &= await SendWriteCommandAsync(session, 0x0002, [stageCount, (byte)(0x55 - stageCount), activeIdx, (byte)(0x55 - activeIdx)], $"DPI Active #{activeIdx + 1}/{stageCount}", log, ct);
        }

        // 3. Individual DPI stages (PAW3395/3950 encoding: step = (DPI / 50) - 1, addr 0x000C + idx*4)
        for (int i = 0; i < stageCount; i++)
        {
            int dpiX = Math.Clamp(profile.DpiStages[i].Dpi, Capabilities.MinDpi, Capabilities.MaxDpi);
            int dpiY = profile.IndependentXY
                ? Math.Clamp(profile.DpiStages[i].DpiY, Capabilities.MinDpi, Capabilities.MaxDpi)
                : dpiX;

            bool stageChanged = fullSync || i >= previous!.DpiStages.Count
                || previous.DpiStages[i].Dpi != dpiX
                || (previous.IndependentXY ? previous.DpiStages[i].DpiY : previous.DpiStages[i].Dpi) != dpiY;

            if (!stageChanged) continue;

            ushort encX = (ushort)Math.Clamp((dpiX / 50) - 1, 0, 599);
            ushort encY = (ushort)Math.Clamp((dpiY / 50) - 1, 0, 599);
            byte loX = (byte)(encX & 0xFF);
            byte loY = (byte)(encY & 0xFF);
            byte hiX = (byte)((encX >> 8) & 0x03);
            byte hiY = (byte)((encY >> 8) & 0x03);
            byte combinedHigh = (byte)((hiY << 2) | hiX);
            byte check = (byte)((0x55 - ((loX + loY + combinedHigh) & 0xFF)) & 0xFF);

            ushort addr = (ushort)(0x000C + (i * 4));
            allOk &= await SendWriteCommandAsync(session, addr, [loX, loY, combinedHigh, check], $"DPI Stage #{i + 1} = {dpiX}x{dpiY}", log, ct);
        }

        // 4. LOD (addr 0x000A): 0x00 = 0.7mm (PAW3950), 0x01 = 1mm, 0x02 = 2mm
        if (fullSync || Math.Abs(previous!.LodMm - profile.LodMm) > 0.05)
        {
            byte lodCode = profile.LodMm <= 0.85 ? (byte)0x00 : profile.LodMm >= 1.5 ? (byte)0x02 : (byte)0x01;
            allOk &= await SendWriteCommandAsync(session, 0x000A, [lodCode, (byte)(0x55 - lodCode)], $"LOD {profile.LodMm:0.0}mm", log, ct);
        }

        // 5. Debounce time (addr 0x00A9)
        if (fullSync || previous!.DebounceMs != profile.DebounceMs)
        {
            byte debounce = (byte)Math.Clamp(profile.DebounceMs, 0, 20);
            allOk &= await SendWriteCommandAsync(session, 0x00A9, [debounce, (byte)(0x55 - debounce)], $"Debounce {debounce}ms", log, ct);
        }

        // 6. Sensor flags: MotionSync (0x00AB), Ripple (0x00AF), AngleSnapping (0x00B1)
        if (fullSync || previous!.MotionSync != profile.MotionSync)
        {
            byte msVal = profile.MotionSync ? (byte)0x01 : (byte)0x00;
            allOk &= await SendWriteCommandAsync(session, 0x00AB, [msVal, (byte)(0x55 - msVal)], $"MotionSync={profile.MotionSync}", log, ct);
        }

        if (fullSync || previous!.RippleControl != profile.RippleControl)
        {
            byte rippleVal = profile.RippleControl ? (byte)0x01 : (byte)0x00;
            allOk &= await SendWriteCommandAsync(session, 0x00AF, [rippleVal, (byte)(0x55 - rippleVal)], $"Ripple={profile.RippleControl}", log, ct);
        }

        if (fullSync || previous!.AngleSnapping != profile.AngleSnapping)
        {
            byte angleVal = profile.AngleSnapping ? (byte)0x01 : (byte)0x00;
            allOk &= await SendWriteCommandAsync(session, 0x00B1, [angleVal, (byte)(0x55 - angleVal)], $"AngleSnap={profile.AngleSnapping}", log, ct);
        }

        // 7. Sleep timeout (addr 0x00AD, encoded in 10-second units)
        if (fullSync || previous!.SleepTimeoutMinutes != profile.SleepTimeoutMinutes)
        {
            byte sleepUnits = (byte)Math.Clamp(profile.SleepTimeoutMinutes * 6, 6, 180);
            allOk &= await SendWriteCommandAsync(session, 0x00AD, [sleepUnits, (byte)(0x55 - sleepUnits)], $"Sleep {profile.SleepTimeoutMinutes}m", log, ct);
        }

        return allOk;
    }

    public static byte EncodePollingRate(int hz) => hz switch
    {
        125 => 0x08,
        250 => 0x04,
        500 => 0x02,
        1000 => 0x01,
        2000 => 0x10,
        4000 => 0x20,
        8000 => 0x40,
        _ => 0x01
    };

    private async Task<bool> SendWriteCommandAsync(
        IHidSession session,
        ushort address,
        byte[] payload,
        string label,
        Action<HidPacketLogEntry>? log,
        CancellationToken ct)
    {
        var tx = BuildPacket(CmdWriteEeprom, address, payload);
        var rx = await TransactAsync(session, tx, label, log, ct);
        if (rx is null || rx.Length < 2)
            return false;

        // Small settle gap so 2.4G dongle/EEPROM finishes committing before next write
        await Task.Delay(12, ct);
        return rx[0] == ReportId || rx[1] == CmdWriteEeprom;
    }

    private async Task<byte[]?> TransactAsync(
        IHidSession session,
        byte[] tx,
        string label,
        Action<HidPacketLogEntry>? log,
        CancellationToken ct)
    {
        bool preferFeature = session.Device.FeatureReportLength >= PacketLength;
        string channel = preferFeature ? "Feature" : "Interrupt";

        log?.Invoke(new HidPacketLogEntry(DateTimeOffset.UtcNow, PacketDirection.Tx, channel, HidPacketHelper.ToHexString(tx), label));

        if (preferFeature)
        {
            var packet = HidPacketHelper.ResizePacket(tx, session.Device.FeatureReportLength);
            bool sent = await session.SetFeatureReportAsync(packet, ct);
            if (!sent)
            {
                log?.Invoke(new HidPacketLogEntry(DateTimeOffset.UtcNow, PacketDirection.Error, channel, string.Empty, $"{label}: SetFeature failed"));
                return null;
            }

            await Task.Delay(18, ct);
            var rx = await session.GetFeatureReportAsync(ReportId, session.Device.FeatureReportLength, ct);
            if (rx is not null)
            {
                log?.Invoke(new HidPacketLogEntry(DateTimeOffset.UtcNow, PacketDirection.Rx, channel, HidPacketHelper.ToHexString(rx), label));
            }
            return rx;
        }
        else
        {
            int outLen = Math.Max(PacketLength, session.Device.OutputReportLength);
            int inLen = Math.Max(PacketLength, session.Device.InputReportLength);
            var packet = HidPacketHelper.ResizePacket(tx, outLen);
            byte expectedCmd = tx[1];

            session.FlushInputQueue();

            bool sent = await session.WriteOutputReportAsync(packet, 250, ct);
            if (!sent)
            {
                log?.Invoke(new HidPacketLogEntry(DateTimeOffset.UtcNow, PacketDirection.Error, channel, string.Empty, $"{label}: WriteOutput failed"));
                return null;
            }

            var rx = await session.ReadMatchingInputReportAsync(
                inLen,
                pkt => pkt.Length >= 2 && ((pkt[0] == ReportId && pkt[1] == expectedCmd) || pkt[0] == expectedCmd),
                timeoutMs: 250,
                maxAttempts: 4,
                ct);
            if (rx is not null)
            {
                log?.Invoke(new HidPacketLogEntry(DateTimeOffset.UtcNow, PacketDirection.Rx, channel, HidPacketHelper.ToHexString(rx), label));
            }
            return rx;
        }
    }
}
