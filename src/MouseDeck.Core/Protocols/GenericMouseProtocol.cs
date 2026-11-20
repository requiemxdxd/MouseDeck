using MouseDeck.Core.Models;
using MouseDeck.Core.Transport;

namespace MouseDeck.Core.Protocols;

/// <summary>
/// Universal fallback protocol for:
///  1. Standard USB HID Battery collections (UsagePage 0x0084/0x0085 or 0x0006:0x0020 Battery Strength).
///  2. ANY standard wired or 2.4GHz HID mouse (UsagePage 0x0001 Usage 0x0002) so MouseDeck
///     always recognizes the user's mouse and offers Windows pointer controls + live Hz tester.
/// </summary>
public sealed class GenericMouseProtocol : IMouseProtocol
{
    public string ProtocolId => "generic-hid-mouse";
    public string DisplayName => "Standard HID (Windows)";
    public int Priority => 10;

    public MouseCapabilities Capabilities { get; } = MouseCapabilities.GenericOsMouse;
    public IReadOnlyList<DeviceMatchRule> SupportedDevices { get; } = [];

    private static readonly string[] NonMouseKeywords =
    [
        "keyboard", "keypad", "клавиатур",
        "headset", "headphone", "earbud", "гарнитур", "microphone", "audio", "speaker",
        "webcam", "camera", "gamepad", "controller", "joystick", "xbox", "dualsense",
        "stream deck", "tablet", "wacom", "touchscreen", "digitizer"
    ];

    internal static bool IsLikelyNonMousePeripheral(string productName)
    {
        if (string.IsNullOrWhiteSpace(productName))
            return false;

        var lower = productName.ToLowerInvariant();
        if (lower.Contains("mouse") || lower.Contains("trackball") || lower.Contains("trackpad") || lower.Contains("мыш"))
            return false;

        return NonMouseKeywords.Any(lower.Contains);
    }

    public int MatchScore(MouseDeviceInfo device)
    {
        if (device.IsVirtual || device.IsBluetooth)
            return 0;

        if (IsLikelyNonMousePeripheral(device.ProductName))
            return 0;

        // Standard HID Battery Strength interface gets higher score than plain pointer collection
        if (device.IsHidBatteryCollection && device.CanOpenReadWrite)
            return 45;

        if (device.IsStandardMouseCollection)
            return 15;

        return 0;
    }

    public MouseTransportKind DetectTransport(MouseDeviceInfo device)
    {
        var name = device.ProductName.ToLowerInvariant();
        if (name.Contains("wireless") || name.Contains("dongle") || name.Contains("2.4g") || name.Contains("receiver"))
            return MouseTransportKind.Wireless24G;
        return MouseTransportKind.WiredUsb;
    }

    public Task<bool> PingWakeStateAsync(IHidSession session, Action<HidPacketLogEntry>? log = null, CancellationToken ct = default)
        => Task.FromResult(true);

    public async Task<BatteryReading?> ReadBatteryAsync(IHidSession session, Action<HidPacketLogEntry>? log = null, CancellationToken ct = default)
    {
        if (!session.Device.IsHidBatteryCollection)
            return null;

        int? pct = await session.TryReadStandardHidBatteryAsync(ct);
        if (pct is int val and >= 1 and <= 100)
        {
            log?.Invoke(new HidPacketLogEntry(DateTimeOffset.UtcNow, PacketDirection.Rx, "HID-Bat", $"{val}%", "Standard HID Battery Strength"));
            return new BatteryReading(val, false, null, IsMouseAwake: true);
        }

        return null;
    }

    public Task<MouseProfile?> ReadConfigAsync(IHidSession session, Action<HidPacketLogEntry>? log = null, CancellationToken ct = default)
        => Task.FromResult<MouseProfile?>(null);

    public Task<bool> ApplyProfileAsync(IHidSession session, MouseProfile profile, Action<HidPacketLogEntry>? log = null, CancellationToken ct = default, MouseProfile? previous = null)
        => Task.FromResult(false);
}
