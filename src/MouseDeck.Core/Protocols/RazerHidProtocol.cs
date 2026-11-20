using MouseDeck.Core.Models;
using MouseDeck.Core.Transport;

namespace MouseDeck.Core.Protocols;

/// <summary>
/// Non-intrusive Razer wireless HID battery protocol (VID 0x1532).
/// Supports Viper V2/V3 Pro, DeathAdder V3 Pro, Basilisk V3 Pro, Orochi V2, Cobra Pro, Naga V2 Pro.
/// Uses standard 90-byte Feature Reports (ReportId 0x00) with XOR CRC at byte[88], zero driver changes,
/// and read-only queries so it never fights Razer Synapse.
/// </summary>
public sealed class RazerHidProtocol : IMouseProtocol
{
    public const ushort RazerVendorId = 0x1532;
    public const int RazerReportSize = 90; // 90 payload bytes + 1 leading ReportId (0x00) = 91 bytes on Windows

    private const byte CmdClassPower = 0x07;
    private const byte CmdGetBattery = 0x80;
    private const byte CmdGetCharging = 0x84;

    private byte? _cachedTxId;

    public string ProtocolId => "razer-hid-90";
    public string DisplayName => "Razer Wireless HID";
    public int Priority => 85;

    public MouseCapabilities Capabilities { get; } = MouseCapabilities.BatteryOnlyWireless;

    public IReadOnlyList<DeviceMatchRule> SupportedDevices { get; } =
    [
        new(RazerVendorId, null, null, FriendlyModelName: "Razer Wireless Mouse")
    ];

    public int MatchScore(MouseDeviceInfo device)
    {
        if (device.IsVirtual || device.IsBluetooth)
            return 0;

        if (device.VendorId != RazerVendorId)
            return 0;

        // Razer control collections expose a 90/91-byte Feature report
        if (device.FeatureReportLength is < RazerReportSize or > RazerReportSize + 2)
            return 0;

        int score = 75;
        if (device.InterfaceNumber == 0) score += 20;
        if (device.IsVendorControlInterface) score += 10;
        return score;
    }

    public MouseTransportKind DetectTransport(MouseDeviceInfo device)
    {
        var name = device.ProductName.ToLowerInvariant();
        if (name.Contains("dongle") || name.Contains("wireless") || name.Contains("hyperspeed") || name.Contains("hyperpolling"))
            return MouseTransportKind.Wireless24G;
        return MouseTransportKind.WiredUsb;
    }

    /// <summary>
    /// Builds a 91-byte Windows Feature Report (byte[0]=0x00 ReportId, bytes[1..90]=Razer 90-byte frame).
    /// </summary>
    public static byte[] BuildFeatureFrame(byte transactionId, byte commandClass, byte commandId, byte dataSize = 0x02)
    {
        var buf = new byte[RazerReportSize + 1];
        buf[0] = 0x00; // Report ID
        buf[1] = 0x00; // Status = New Command
        buf[2] = transactionId;
        buf[3] = 0x00; // Remaining packets hi
        buf[4] = 0x00; // Remaining packets lo
        buf[5] = 0x00; // Protocol type
        buf[6] = dataSize;
        buf[7] = commandClass;
        buf[8] = commandId;

        // Razer CRC = XOR of payload bytes [2..87] (offset +1 due to leading ReportId byte[0])
        buf[89] = ComputeRazerCrc(buf.AsSpan(1, RazerReportSize));
        return buf;
    }

    public static byte ComputeRazerCrc(ReadOnlySpan<byte> frame90)
    {
        byte crc = 0;
        int end = Math.Min(88, frame90.Length);
        for (int i = 2; i < end; i++)
            crc ^= frame90[i];
        return crc;
    }

    public static int? ParseBatteryPercent(ReadOnlySpan<byte> rx)
    {
        if (rx.Length < 12) return null;
        int off = rx.Length >= RazerReportSize + 1 ? 1 : 0;
        byte status = rx[off + 0];
        if (status != 0x02) return null; // 0x02 = Successful response

        byte rawLevel = rx[off + 9]; // args[1] = 0..255
        if (rawLevel == 0) return 0;
        return Math.Clamp((int)Math.Round(rawLevel * 100.0 / 255.0), 1, 100);
    }

    public static bool? ParseChargingFlag(ReadOnlySpan<byte> rx)
    {
        if (rx.Length < 12) return null;
        int off = rx.Length >= RazerReportSize + 1 ? 1 : 0;
        if (rx[off + 0] != 0x02) return null;
        return rx[off + 9] != 0;
    }

    public async Task<bool> PingWakeStateAsync(IHidSession session, Action<HidPacketLogEntry>? log = null, CancellationToken ct = default)
    {
        var reading = await ReadBatteryAsync(session, log, ct);
        return reading is { IsMouseAwake: true };
    }

    public async Task<BatteryReading?> ReadBatteryAsync(IHidSession session, Action<HidPacketLogEntry>? log = null, CancellationToken ct = default)
    {
        byte[] txCandidates = _cachedTxId.HasValue
            ? [_cachedTxId.Value, 0x1F, 0x3F, 0xFF]
            : [0x1F, 0x3F, 0xFF];

        foreach (byte txId in txCandidates.Distinct())
        {
            var rxBat = await TransactRazerAsync(session, txId, CmdClassPower, CmdGetBattery, "Razer Battery", log, ct);
            if (rxBat is null) continue;

            int? pct = ParseBatteryPercent(rxBat);
            if (pct is null) continue;

            _cachedTxId = txId;

            bool isCharging = false;
            var rxChg = await TransactRazerAsync(session, txId, CmdClassPower, CmdGetCharging, "Razer Charging", log, ct);
            if (rxChg is not null)
            {
                isCharging = ParseChargingFlag(rxChg) ?? false;
            }

            if (pct.Value == 0 && !isCharging)
                return new BatteryReading(0, false, null, IsMouseAwake: false);

            return new BatteryReading(pct.Value, isCharging, null, IsMouseAwake: true);
        }

        return null;
    }

    public Task<MouseProfile?> ReadConfigAsync(IHidSession session, Action<HidPacketLogEntry>? log = null, CancellationToken ct = default)
        => Task.FromResult<MouseProfile?>(null);

    public Task<bool> ApplyProfileAsync(
        IHidSession session,
        MouseProfile profile,
        Action<HidPacketLogEntry>? log = null,
        CancellationToken ct = default,
        MouseProfile? previous = null)
        => Task.FromResult(false);

    private static async Task<byte[]?> TransactRazerAsync(
        IHidSession session,
        byte txId,
        byte cmdClass,
        byte cmdId,
        string label,
        Action<HidPacketLogEntry>? log,
        CancellationToken ct)
    {
        int featLen = Math.Max(RazerReportSize + 1, session.Device.FeatureReportLength);
        var tx = HidPacketHelper.ResizePacket(BuildFeatureFrame(txId, cmdClass, cmdId), featLen);
        log?.Invoke(new HidPacketLogEntry(DateTimeOffset.UtcNow, PacketDirection.Tx, "RazerFeat", HidPacketHelper.ToHexString(tx.AsSpan(0, 14)), label));

        if (!await session.SetFeatureReportAsync(tx, ct))
            return null;

        await Task.Delay(35, ct);
        var rx = await session.GetFeatureReportAsync(0x00, featLen, ct);
        if (rx is not null)
        {
            log?.Invoke(new HidPacketLogEntry(DateTimeOffset.UtcNow, PacketDirection.Rx, "RazerFeat", HidPacketHelper.ToHexString(rx.AsSpan(0, Math.Min(14, rx.Length))), label));
        }
        return rx;
    }
}
