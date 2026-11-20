namespace MouseDeck.Core.Protocols;

public sealed record MouseCapabilities
{
    public int MinDpi { get; init; } = 50;
    public int MaxDpi { get; init; } = 26000;
    public int DpiStep { get; init; } = 50;
    public int MaxDpiStages { get; init; } = 6;
    public IReadOnlyList<int> SupportedPollingRatesHz { get; init; } = [125, 250, 500, 1000];
    public IReadOnlyList<double> SupportedLodMm { get; init; } = [1.0, 2.0];
    public int MinDebounceMs { get; init; } = 0;
    public int MaxDebounceMs { get; init; } = 20;
    public bool SupportsMotionSync { get; init; } = true;
    public bool SupportsRippleControl { get; init; } = true;
    public bool SupportsAngleSnapping { get; init; } = true;
    public bool SupportsHighSpeedMode { get; init; }
    public bool SupportsBatteryRead { get; init; } = true;
    public bool SupportsHardwareSettings { get; init; } = true;
    public bool IsWriteOnlyConfig { get; init; } = true;

    public static MouseCapabilities DefaultPaw3395 { get; } = new()
    {
        MinDpi = 50,
        MaxDpi = 26000,
        DpiStep = 50,
        MaxDpiStages = 6,
        SupportedPollingRatesHz = [125, 250, 500, 1000],
        SupportedLodMm = [1.0, 2.0],
        MinDebounceMs = 0,
        MaxDebounceMs = 20,
        SupportsMotionSync = true,
        SupportsRippleControl = true,
        SupportsAngleSnapping = true,
        SupportsHighSpeedMode = false,
        SupportsBatteryRead = true,
        SupportsHardwareSettings = true,
        IsWriteOnlyConfig = true
    };

    public static MouseCapabilities Paw3950HighRate { get; } = new()
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
        SupportsHardwareSettings = true,
        IsWriteOnlyConfig = true
    };

    public static MouseCapabilities BatteryOnlyWireless { get; } = new()
    {
        MinDpi = 400,
        MaxDpi = 25600,
        DpiStep = 50,
        MaxDpiStages = 5,
        SupportedPollingRatesHz = [125, 500, 1000],
        SupportedLodMm = [1.0, 2.0],
        SupportsMotionSync = false,
        SupportsRippleControl = false,
        SupportsAngleSnapping = false,
        SupportsHighSpeedMode = false,
        SupportsBatteryRead = true,
        SupportsHardwareSettings = false,
        IsWriteOnlyConfig = false
    };

    public static MouseCapabilities GenericOsMouse { get; } = new()
    {
        MinDpi = 400,
        MaxDpi = 16000,
        DpiStep = 50,
        MaxDpiStages = 4,
        SupportedPollingRatesHz = [125, 500, 1000],
        SupportedLodMm = [1.0],
        SupportsMotionSync = false,
        SupportsRippleControl = false,
        SupportsAngleSnapping = false,
        SupportsHighSpeedMode = false,
        SupportsBatteryRead = false,
        SupportsHardwareSettings = false,
        IsWriteOnlyConfig = false
    };
}
