namespace MouseDeck.Core.Models;

public enum MouseConnectionStatus
{
    Disconnected,
    Connecting,
    Active,
    Sleeping,
    Charging
}

public enum MouseTransportKind
{
    Unknown,
    Wireless24G,
    WiredUsb,
    Bluetooth,
    Virtual
}

public enum ProfileSwitchSource
{
    Default,
    Manual,
    AutoProcess,
    Hotkey
}

public sealed record BatteryReading(
    int Percentage,
    bool IsCharging,
    int? VoltageMv,
    bool IsMouseAwake);

public sealed record BatteryHistoryPoint(
    DateTimeOffset TimestampUtc,
    int Percent,
    bool IsCharging,
    int? VoltageMv = null);

public enum HardwareConfigOrigin
{
    NotReadPreserved,
    ReadFromDevice,
    AppliedByUser
}

public sealed record DiscoveredMouseSummary(
    string DevicePath,
    string DeviceKey,
    string DisplayName,
    string ProtocolId,
    string ProtocolName,
    MouseTransportKind Transport,
    bool SupportsBattery,
    bool SupportsHardwareSettings,
    bool IsSelected);

public sealed record MouseRuntimeState
{
    public string DeviceName { get; init; } = "No mouse detected";
    public string DeviceKey { get; init; } = string.Empty;
    public string DevicePath { get; init; } = string.Empty;
    public string DeviceInterfaceInfo { get; init; } = "—";
    public string ProtocolId { get; init; } = string.Empty;
    public string ProtocolName { get; init; } = "No driver";
    public MouseConnectionStatus Status { get; init; } = MouseConnectionStatus.Disconnected;
    public MouseTransportKind Transport { get; init; } = MouseTransportKind.Unknown;
    public HardwareConfigOrigin ConfigOrigin { get; init; } = HardwareConfigOrigin.NotReadPreserved;

    /// <summary>
    /// Last known battery percentage (0-100). Retained while in Sleeping state.
    /// </summary>
    public int? BatteryPercent { get; init; }
    public int? VoltageMv { get; init; }
    public bool IsCharging { get; init; }
    public DateTimeOffset? LastBatteryUpdateUtc { get; init; }
    public DateTimeOffset? LastSeenActiveUtc { get; init; }
    public double? MeasuredDrainPercentPerHour { get; init; }
    public double? MeasuredChargePercentPerMinute { get; init; }
    public IReadOnlyList<BatteryHistoryPoint> BatteryHistory { get; init; } = [];

    public bool HasPendingSync { get; init; }
    public DateTimeOffset? LastSyncUtc { get; init; }
    public string? LastSyncError { get; init; }

    public string ActiveProfileId { get; init; } = string.Empty;
    public string ActiveProfileName { get; init; } = string.Empty;
    public int CurrentDpi { get; init; } = 800;
    public int CurrentPollingRateHz { get; init; } = 1000;
    public ProfileSwitchSource ProfileSource { get; init; } = ProfileSwitchSource.Default;
    public string? ActiveForegroundProcess { get; init; }

    public string? DetectedVendorSoftware { get; init; }
    public IReadOnlyList<DiscoveredMouseSummary> AvailableMice { get; init; } = [];
}
