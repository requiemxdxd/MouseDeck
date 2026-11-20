using MouseDeck.Core.Models;
using MouseDeck.Core.Transport;

namespace MouseDeck.Core.Protocols;

/// <summary>
/// Universal protocol for ANY Bluetooth LE or Classic Bluetooth mouse connected to Windows 11.
/// Reads battery directly from Windows PnP DEVPKEY_Bluetooth_Battery / GATT Battery Service (0x180F)
/// with 0% idle CPU and zero vendor-software conflicts.
/// </summary>
public sealed class BluetoothMouseProtocol : IMouseProtocol
{
    public string ProtocolId => "bluetooth-gatt-pnp";
    public string DisplayName => "Bluetooth (Windows GATT / PnP)";
    public int Priority => 70;

    public MouseCapabilities Capabilities { get; } = MouseCapabilities.BatteryOnlyWireless;
    public IReadOnlyList<DeviceMatchRule> SupportedDevices { get; } = [];

    public int MatchScore(MouseDeviceInfo device)
    {
        if (!device.IsBluetooth)
            return 0;

        int score = 65;
        if (device.IsBluetoothConnected) score += 25;
        if (device.OsBatteryPercent.HasValue) score += 20;
        return score;
    }

    public MouseTransportKind DetectTransport(MouseDeviceInfo device) => MouseTransportKind.Bluetooth;

    public Task<bool> PingWakeStateAsync(IHidSession session, Action<HidPacketLogEntry>? log = null, CancellationToken ct = default)
        => Task.FromResult(session.Device.IsBluetoothConnected || session.Device.OsBatteryPercent.HasValue);

    public async Task<BatteryReading?> ReadBatteryAsync(IHidSession session, Action<HidPacketLogEntry>? log = null, CancellationToken ct = default)
    {
        int? pct = await session.TryReadStandardHidBatteryAsync(ct) ?? session.Device.OsBatteryPercent;
        if (!pct.HasValue)
        {
            return new BatteryReading(0, false, null, IsMouseAwake: false);
        }

        int clamped = Math.Clamp(pct.Value, 1, 100);
        log?.Invoke(new HidPacketLogEntry(DateTimeOffset.UtcNow, PacketDirection.Rx, "BT-PnP", $"{clamped}%", $"Bluetooth Battery: {session.Device.ProductName}"));
        return new BatteryReading(clamped, false, null, IsMouseAwake: session.Device.IsBluetoothConnected || clamped > 0);
    }

    public Task<MouseProfile?> ReadConfigAsync(IHidSession session, Action<HidPacketLogEntry>? log = null, CancellationToken ct = default)
        => Task.FromResult<MouseProfile?>(null);

    public Task<bool> ApplyProfileAsync(IHidSession session, MouseProfile profile, Action<HidPacketLogEntry>? log = null, CancellationToken ct = default, MouseProfile? previous = null)
        => Task.FromResult(false);
}
