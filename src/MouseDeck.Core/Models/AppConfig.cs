using System.Text.Json;
using System.Text.Json.Serialization;

namespace MouseDeck.Core.Models;

[JsonConverter(typeof(TrayIconStyleJsonConverter))]
public enum TrayIconStyle
{
    /// <summary>Native Windows 11 taskbar pill [🖱 30%] right next to the tray chevron.</summary>
    TaskbarPillIconAndPercent,
    /// <summary>Native Windows 11 taskbar pill [MAD 8K  30%] right next to the tray chevron.</summary>
    TaskbarPillNameAndPercent,
    /// <summary>Pure 16x16 tray icon: huge crisp percentage digits + thin bottom status bar.</summary>
    TrayLargePercent,
    /// <summary>Pure 16x16 tray icon: minimalist mouse outline filled vertically by charge level.</summary>
    TrayMouseBatteryFill,
    /// <summary>Pure 16x16 / 24x24 tray icon: circular charge ring + centered percentage.</summary>
    TrayRingBadge
}

/// <summary>
/// Tolerant enum converter so older config.json files (NumberAndSilhouette, DualMouseAndPercent, etc.)
/// upgrade smoothly without resetting user profiles or rules.
/// </summary>
public sealed class TrayIconStyleJsonConverter : JsonConverter<TrayIconStyle>
{
    public override TrayIconStyle Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int num))
        {
            return Enum.IsDefined(typeof(TrayIconStyle), num)
                ? (TrayIconStyle)num
                : TrayIconStyle.TaskbarPillIconAndPercent;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var raw = reader.GetString()?.Trim();
            if (Enum.TryParse<TrayIconStyle>(raw, ignoreCase: true, out var parsed))
                return parsed;

            // Graceful migration from v0.1..v0.8 styles
            return raw?.ToLowerInvariant() switch
            {
                "dualmouseandpercent" => TrayIconStyle.TaskbarPillIconAndPercent,
                "numberandsilhouette" => TrayIconStyle.TrayLargePercent,
                "colorbarplot" or "colorbarandsilhouette" => TrayIconStyle.TrayMouseBatteryFill,
                "mouseonly" => TrayIconStyle.TrayMouseBatteryFill,
                _ => TrayIconStyle.TaskbarPillIconAndPercent
            };
        }

        return TrayIconStyle.TaskbarPillIconAndPercent;
    }

    public override void Write(Utf8JsonWriter writer, TrayIconStyle value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}

public enum FlyoutBackdropStyle
{
    Acrylic,
    Mica
}

public enum AppLanguage
{
    Russian,
    English
}

public enum ChecksumAlgorithm
{
    None,
    CompX55,
    Sum8,
    Xor8
}

public sealed class ProcessProfileRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string ProcessName { get; set; } = string.Empty;
    public string ProfileId { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}

public sealed class HotkeyBinding
{
    public string Action { get; set; } = "CycleProfile";
    public string Label { get; set; } = "Next profile";
    /// <summary>
    /// Win32 MOD_ALT=0x1, MOD_CONTROL=0x2, MOD_SHIFT=0x4, MOD_WIN=0x8
    /// </summary>
    public uint Modifiers { get; set; }
    public uint VirtualKey { get; set; }
    public string DisplayText { get; set; } = "Ctrl+Alt+M";
    public bool Enabled { get; set; } = true;
}

public sealed class CachedDeviceBattery
{
    public string DeviceKey { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public int? LastKnownPercent { get; set; }
    public int? LastKnownVoltageMv { get; set; }
    public DateTimeOffset? LastUpdatedUtc { get; set; }
    public string? LastSyncedProfileHash { get; set; }
    public List<BatteryHistoryPoint> History { get; set; } = [];
}

public sealed class AppConfig
{
    public int SchemaVersion { get; set; } = 2;
    public List<MouseProfile> Profiles { get; set; } = [];
    public string ActiveProfileId { get; set; } = "eco";
    public string DefaultDesktopProfileId { get; set; } = "eco";
    public string GamingQuickToggleProfileId { get; set; } = "gaming";

    public bool AutoSwitchEnabled { get; set; }
    public bool ManualProfileOverride { get; set; }
    public List<ProcessProfileRule> ProcessRules { get; set; } = [];
    public List<HotkeyBinding> Hotkeys { get; set; } = [];

    public bool LowBatteryAlertEnabled { get; set; } = true;
    public int LowBatteryThresholdPercent { get; set; } = 15;
    public bool FullBatteryAlertEnabled { get; set; } = true;
    public bool AutoStartEnabled { get; set; }
    public bool UnlockEditingOnStartup { get; set; }
    public TrayIconStyle TrayIconStyle { get; set; } = TrayIconStyle.TaskbarPillIconAndPercent;
    public FlyoutBackdropStyle BackdropStyle { get; set; } = FlyoutBackdropStyle.Acrylic;
    public AppLanguage Language { get; set; } = AppLanguage.Russian;
    public bool IsFirstRunSetupCompleted { get; set; }
    public bool PinWindowOpen { get; set; }
    public bool EnableVirtualMouse { get; set; }
    public string? SelectedDevicePathOverride { get; set; }

    public Dictionary<string, CachedDeviceBattery> DeviceCache { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static AppConfig CreateDefault()
    {
        var eco = new MouseProfile
        {
            Id = "eco",
            Name = "Eco / Desktop",
            IconKey = "Eco",
            ActiveDpiStageIndex = 1,
            DpiStages =
            [
                new DpiStage { Dpi = 800, DpiY = 800, ColorHex = "#FF38BDF8" },
                new DpiStage { Dpi = 1200, DpiY = 1200, ColorHex = "#FF22C55E" },
                new DpiStage { Dpi = 1600, DpiY = 1600, ColorHex = "#FFA855F7" },
                new DpiStage { Dpi = 2400, DpiY = 2400, ColorHex = "#FFF59E0B" }
            ],
            PollingRateHz = 500,
            LodMm = 1.0,
            DebounceMs = 4,
            MotionSync = false,
            RippleControl = false,
            AngleSnapping = false,
            HighSpeedMode = false,
            SleepTimeoutMinutes = 3
        };

        var gaming = new MouseProfile
        {
            Id = "gaming",
            Name = "Gaming",
            IconKey = "Gaming",
            ActiveDpiStageIndex = 2,
            DpiStages =
            [
                new DpiStage { Dpi = 400, DpiY = 400, ColorHex = "#FFEF4444" },
                new DpiStage { Dpi = 800, DpiY = 800, ColorHex = "#FF38BDF8" },
                new DpiStage { Dpi = 1600, DpiY = 1600, ColorHex = "#FF22C55E" },
                new DpiStage { Dpi = 3200, DpiY = 3200, ColorHex = "#FFEAB308" }
            ],
            PollingRateHz = 1000,
            LodMm = 1.0,
            DebounceMs = 2,
            MotionSync = true,
            RippleControl = false,
            AngleSnapping = false,
            HighSpeedMode = true,
            SleepTimeoutMinutes = 5
        };

        var esports = new MouseProfile
        {
            Id = "esports",
            Name = "Esports 4K",
            IconKey = "Esports",
            ActiveDpiStageIndex = 1,
            DpiStages =
            [
                new DpiStage { Dpi = 400, DpiY = 400, ColorHex = "#FFEF4444" },
                new DpiStage { Dpi = 800, DpiY = 800, ColorHex = "#FF06B6D4" },
                new DpiStage { Dpi = 1600, DpiY = 1600, ColorHex = "#FFF97316" },
                new DpiStage { Dpi = 3200, DpiY = 3200, ColorHex = "#FFEC4899" }
            ],
            PollingRateHz = 4000,
            LodMm = 0.7,
            DebounceMs = 0,
            MotionSync = true,
            RippleControl = false,
            AngleSnapping = false,
            HighSpeedMode = true,
            SleepTimeoutMinutes = 10
        };

        return new AppConfig
        {
            SchemaVersion = 2,
            Profiles = [eco, gaming, esports],
            ActiveProfileId = eco.Id,
            DefaultDesktopProfileId = eco.Id,
            GamingQuickToggleProfileId = gaming.Id,
            AutoSwitchEnabled = false,
            ManualProfileOverride = false,
            ProcessRules =
            [
                new ProcessProfileRule { Id = "r1", ProcessName = "cs2.exe", ProfileId = gaming.Id, Enabled = true },
                new ProcessProfileRule { Id = "r2", ProcessName = "valorant-win64-shipping.exe", ProfileId = esports.Id, Enabled = true },
                new ProcessProfileRule { Id = "r3", ProcessName = "r5apex.exe", ProfileId = gaming.Id, Enabled = true }
            ],
            Hotkeys =
            [
                new HotkeyBinding
                {
                    Action = "CycleProfile",
                    Label = "Next profile",
                    Modifiers = 0x0002 | 0x0001, // Ctrl + Alt
                    VirtualKey = 0x4D, // M
                    DisplayText = "Ctrl+Alt+M",
                    Enabled = true
                },
                new HotkeyBinding
                {
                    Action = "ToggleEcoGaming",
                    Label = "Toggle Eco / Gaming",
                    Modifiers = 0x0002 | 0x0001, // Ctrl + Alt
                    VirtualKey = 0x47, // G
                    DisplayText = "Ctrl+Alt+G",
                    Enabled = true
                }
            ],
            LowBatteryAlertEnabled = true,
            LowBatteryThresholdPercent = 15,
            FullBatteryAlertEnabled = true,
            AutoStartEnabled = false,
            UnlockEditingOnStartup = false,
            TrayIconStyle = TrayIconStyle.TaskbarPillIconAndPercent,
            BackdropStyle = FlyoutBackdropStyle.Acrylic,
            EnableVirtualMouse = false
        };
    }
}
