using MouseDeck.Core.Models;
using MouseDeck.Core.Transport;

namespace MouseDeck.Core.Protocols;

public sealed record DeviceMatchRule(
    ushort VendorId,
    ushort? ProductId = null,
    ushort? UsagePage = null,
    ushort? Usage = null,
    string? FriendlyModelName = null,
    MouseTransportKind TransportHint = MouseTransportKind.Wireless24G);

public interface IMouseProtocol
{
    string ProtocolId { get; }
    string DisplayName { get; }
    int Priority { get; }
    IReadOnlyList<DeviceMatchRule> SupportedDevices { get; }
    MouseCapabilities Capabilities { get; }

    /// <summary>
    /// Returns a positive match score if this protocol handles the given HID interface, or 0 if unsupported.
    /// Higher score wins in ProtocolRegistry.
    /// </summary>
    int MatchScore(MouseDeviceInfo device);

    MouseTransportKind DetectTransport(MouseDeviceInfo device);

    /// <summary>
    /// Checks whether the mouse behind the wireless dongle is awake without disturbing sensor sleep when possible.
    /// </summary>
    Task<bool> PingWakeStateAsync(IHidSession session, Action<HidPacketLogEntry>? log = null, CancellationToken ct = default);

    /// <summary>
    /// Reads battery percentage, charging status, optional voltage, and awake flag.
    /// Returns null when the mouse does not answer (e.g. deep sleep).
    /// </summary>
    Task<BatteryReading?> ReadBatteryAsync(IHidSession session, Action<HidPacketLogEntry>? log = null, CancellationToken ct = default);

    /// <summary>
    /// Reads the current hardware configuration stored in the mouse without modifying any device registers.
    /// Returns null when the mouse is write-only or currently asleep.
    /// </summary>
    Task<MouseProfile?> ReadConfigAsync(IHidSession session, Action<HidPacketLogEntry>? log = null, CancellationToken ct = default);

    /// <summary>
    /// Pushes a complete profile or only changed registers (when <paramref name="previous"/> is provided).
    /// Called ONLY when the user explicitly modifies/applies settings — NEVER on startup.
    /// </summary>
    Task<bool> ApplyProfileAsync(
        IHidSession session,
        MouseProfile profile,
        Action<HidPacketLogEntry>? log = null,
        CancellationToken ct = default,
        MouseProfile? previous = null);
}
