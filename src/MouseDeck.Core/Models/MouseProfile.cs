using System.Security.Cryptography;
using System.Text;

namespace MouseDeck.Core.Models;

public sealed class DpiStage
{
    public int Dpi { get; set; } = 800;
    public int DpiY { get; set; } = 800;
    public string ColorHex { get; set; } = "#FF38BDF8";

    public DpiStage Clone() => new()
    {
        Dpi = Dpi,
        DpiY = DpiY,
        ColorHex = ColorHex
    };
}

public sealed class MouseProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "New profile";
    public string IconKey { get; set; } = "Mouse";

    public int ActiveDpiStageIndex { get; set; } = 1;
    public bool IndependentXY { get; set; }
    public List<DpiStage> DpiStages { get; set; } =
    [
        new DpiStage { Dpi = 400, DpiY = 400, ColorHex = "#FFEF4444" },
        new DpiStage { Dpi = 800, DpiY = 800, ColorHex = "#FF38BDF8" },
        new DpiStage { Dpi = 1600, DpiY = 1600, ColorHex = "#FF22C55E" },
        new DpiStage { Dpi = 3200, DpiY = 3200, ColorHex = "#FFEAB308" }
    ];

    public int PollingRateHz { get; set; } = 1000;
    public double LodMm { get; set; } = 1.0;
    public int DebounceMs { get; set; } = 2;
    public int AngleTuningDegrees { get; set; }
    public bool MotionSync { get; set; } = true;
    public bool RippleControl { get; set; }
    public bool AngleSnapping { get; set; }
    public bool HighSpeedMode { get; set; }
    public int SleepTimeoutMinutes { get; set; } = 5;

    public Dictionary<string, string> ButtonMappings { get; set; } = CreateDefaultButtonMappings();

    public static Dictionary<string, string> CreateDefaultButtonMappings() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Left"] = "LMB (Primary)",
        ["Right"] = "RMB (Context menu)",
        ["Middle"] = "MMB (Middle click)",
        ["Forward"] = "Forward (M5)",
        ["Back"] = "Back (M4)",
        ["Dpi"] = "DPI Cycle +"
    };

    public int CurrentDpi
    {
        get
        {
            if (DpiStages.Count == 0) return 800;
            var idx = Math.Clamp(ActiveDpiStageIndex, 0, DpiStages.Count - 1);
            return DpiStages[idx].Dpi;
        }
    }

    public int CurrentDpiY
    {
        get
        {
            if (DpiStages.Count == 0) return 800;
            var idx = Math.Clamp(ActiveDpiStageIndex, 0, DpiStages.Count - 1);
            return IndependentXY ? DpiStages[idx].DpiY : DpiStages[idx].Dpi;
        }
    }

    public string CurrentStageColorHex
    {
        get
        {
            if (DpiStages.Count == 0) return "#FF38BDF8";
            var idx = Math.Clamp(ActiveDpiStageIndex, 0, DpiStages.Count - 1);
            return DpiStages[idx].ColorHex;
        }
    }

    public double PollingLatencyMs => PollingRateHz > 0 ? 1000.0 / PollingRateHz : 1.0;

    /// <summary>
    /// Full-charge continuous active runtime hours for a typical 250–300mAh PAW3395/PAW3950 wireless mouse.
    /// </summary>
    public double EstimateFullChargeActiveHours()
    {
        double fullChargeHours = PollingRateHz switch
        {
            <= 125 => 80,
            <= 250 => 72,
            <= 500 => 62,
            <= 1000 => 48,
            <= 2000 => 26,
            <= 4000 => 15,
            _ => 9
        };

        if (HighSpeedMode) fullChargeHours *= 0.85;
        if (MotionSync) fullChargeHours *= 0.96;

        return fullChargeHours;
    }

    /// <summary>
    /// Estimates remaining continuous active runtime hours for a typical 250–300mAh PAW3395/PAW3950 wireless mouse.
    /// </summary>
    public double EstimateRemainingHours(int batteryPercent)
    {
        double fullChargeHours = EstimateFullChargeActiveHours();
        return Math.Max(0.25, fullChargeHours * (Math.Clamp(batteryPercent, 1, 100) / 100.0));
    }

    public string ComputeSyncHash()
    {
        var sb = new StringBuilder();
        sb.Append(ActiveDpiStageIndex).Append('|')
          .Append(IndependentXY).Append('|')
          .Append(PollingRateHz).Append('|')
          .Append(LodMm.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
          .Append(DebounceMs).Append('|')
          .Append(AngleTuningDegrees).Append('|')
          .Append(MotionSync).Append('|')
          .Append(RippleControl).Append('|')
          .Append(AngleSnapping).Append('|')
          .Append(HighSpeedMode).Append('|')
          .Append(SleepTimeoutMinutes);

        foreach (var stage in DpiStages)
        {
            sb.Append('|').Append(stage.Dpi).Append('x').Append(stage.DpiY).Append(':').Append(stage.ColorHex);
        }

        if (ButtonMappings is not null)
        {
            foreach (var kv in ButtonMappings.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append('|').Append(kv.Key).Append('=').Append(kv.Value);
            }
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash)[..16];
    }

    public MouseProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        IconKey = IconKey,
        ActiveDpiStageIndex = ActiveDpiStageIndex,
        IndependentXY = IndependentXY,
        DpiStages = DpiStages.Select(s => s.Clone()).ToList(),
        PollingRateHz = PollingRateHz,
        LodMm = LodMm,
        DebounceMs = DebounceMs,
        AngleTuningDegrees = AngleTuningDegrees,
        MotionSync = MotionSync,
        RippleControl = RippleControl,
        AngleSnapping = AngleSnapping,
        HighSpeedMode = HighSpeedMode,
        SleepTimeoutMinutes = SleepTimeoutMinutes,
        ButtonMappings = new Dictionary<string, string>(ButtonMappings ?? CreateDefaultButtonMappings(), StringComparer.OrdinalIgnoreCase)
    };
}
