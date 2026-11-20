using MouseDeck.Core.Models;

namespace MouseDeck.Core.Services;

public sealed record ChargingEstimate(
    TimeSpan RemainingTime,
    double? MeasuredPercentPerMinute,
    bool IsCalibrated);

public sealed record DischargeEstimate(
    TimeSpan RemainingTime,
    double? MeasuredPercentPerHour,
    bool IsCalibrated);

/// <summary>
/// Maintains a lightweight 24-hour rolling battery history per device and computes:
///  1. Active-only measured discharge rate (%/hour) via regression over continuous usage segments
///     (excluding overnight sleep/PC-off gaps > 20 min) blended with polling-rate prior + Li-Ion voltage curve.
///  2. Accurate Li-Ion CC-CV charging time remaining (0..80% constant-current + 80..100% CV taper),
///     calibrated live from the actual mouse + USB port charging speed.
/// </summary>
public static class BatteryHistoryService
{
    public const int MaxHistoryPoints = 96;
    private static readonly TimeSpan MaxHistoryAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan DischargeRecordInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ChargingRecordInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxContinuousDischargeGap = TimeSpan.FromMinutes(25);
    private static readonly TimeSpan MaxContinuousChargingGap = TimeSpan.FromMinutes(10);

    // Default prior for 200-350mAh wireless gaming mouse USB charging (~90 min for 0->100% = 120 CC-units)
    private const double PriorChargingCcUnitsPerMinute = 120.0 / 90.0;

    // Typical single-cell 3.7V Li-Ion resting discharge curve (mV -> %)
    private static readonly (int Mv, double Pct)[] LiIonVoltageCurve =
    [
        (3350, 0.0),
        (3500, 4.0),
        (3580, 9.0),
        (3640, 16.0),
        (3690, 24.0),
        (3740, 34.0),
        (3790, 45.0),
        (3850, 58.0),
        (3920, 70.0),
        (4000, 82.0),
        (4100, 92.0),
        (4180, 100.0)
    ];

    public static void RecordPoint(
        CachedDeviceBattery cache,
        int percent,
        bool isCharging,
        DateTimeOffset timestampUtc,
        int? voltageMv = null)
    {
        cache.History ??= [];

        int clamped = Math.Clamp(percent, 0, 100);
        var cutoff = timestampUtc - MaxHistoryAge;
        cache.History.RemoveAll(p => p.TimestampUtc < cutoff);

        if (cache.History.Count > 0)
        {
            var last = cache.History[^1];
            bool sameState = last.Percent == clamped && last.IsCharging == isCharging;
            bool significantVoltageShift = isCharging && voltageMv.HasValue && last.VoltageMv.HasValue &&
                                           Math.Abs(voltageMv.Value - last.VoltageMv.Value) >= 12;
            var minInterval = isCharging ? ChargingRecordInterval : DischargeRecordInterval;

            if (sameState && !significantVoltageShift && (timestampUtc - last.TimestampUtc) < minInterval)
                return;
        }

        cache.History.Add(new BatteryHistoryPoint(timestampUtc, clamped, isCharging, voltageMv));
        while (cache.History.Count > MaxHistoryPoints)
            cache.History.RemoveAt(0);
    }

    /// <summary>
    /// Refines coarse 5%/10%-step firmware battery percentages using live Li-Ion resting voltage
    /// when discharging, clamped tightly near the reported % so it never contradicts firmware readings.
    /// </summary>
    public static double ComputeEffectiveDischargePercent(int reportedPercent, int? voltageMv)
    {
        double clamped = Math.Clamp(reportedPercent, 0, 100);
        if (voltageMv is not int mv || mv < 3250 || mv > 4260)
            return clamped;

        double voltagePct = InterpolateLiIonVoltagePercent(mv);
        double boundedVoltagePct = Math.Clamp(voltagePct, Math.Max(0.0, clamped - 5.0), Math.Min(100.0, clamped + 3.0));
        return Math.Clamp((clamped * 0.65) + (boundedVoltagePct * 0.35), 0.5, 100.0);
    }

    public static double InterpolateLiIonVoltagePercent(int voltageMv)
    {
        if (voltageMv <= LiIonVoltageCurve[0].Mv) return 0.0;
        if (voltageMv >= LiIonVoltageCurve[^1].Mv) return 100.0;

        for (int i = 1; i < LiIonVoltageCurve.Length; i++)
        {
            var (mv0, p0) = LiIonVoltageCurve[i - 1];
            var (mv1, p1) = LiIonVoltageCurve[i];
            if (voltageMv <= mv1)
            {
                double t = (voltageMv - mv0) / (double)(mv1 - mv0);
                return p0 + t * (p1 - p0);
            }
        }

        return 100.0;
    }

    /// <summary>
    /// Maps a Li-Ion state-of-charge percentage (0..100) into equivalent Constant-Current (CC) charge units:
    ///  - 0..80%: linear 1.0 unit per % (CC phase)
    ///  - 80..100%: smooth CV saturation taper (1.0x at 80% ramping to 3.0x at 100%, avg 2.0x -> 40 units)
    /// Total 0..100% = 120 equivalent CC units.
    /// </summary>
    public static double ToEquivalentCcPercent(double percent)
    {
        double p = Math.Clamp(percent, 0.0, 100.0);
        if (p <= 80.0)
            return p;

        double cv = p - 80.0;
        return 80.0 + cv + (0.05 * cv * cv);
    }

    /// <summary>
    /// Computes measured active discharge rate (%/hour) across the latest continuous non-charging segment,
    /// ignoring overnight sleep or PC-off gaps (> 25 minutes) and using OLS regression to suppress 5%-step jitter.
    /// </summary>
    public static double? ComputeMeasuredDrainRatePerHour(IReadOnlyList<BatteryHistoryPoint>? history)
    {
        var (rate, _, _) = AnalyzeDischargeSegment(history);
        return rate;
    }

    /// <summary>
    /// Computes measured charging speed (%/min) across the latest continuous charging segment.
    /// </summary>
    public static double? ComputeMeasuredChargeRatePerMinute(IReadOnlyList<BatteryHistoryPoint>? history)
    {
        var (_, rawPctPerMin, _, _) = AnalyzeChargingSegment(history);
        return rawPctPerMin;
    }

    /// <summary>
    /// Computes a smooth, calibrated discharge remaining-time estimate combining:
    ///  - Active-only measured drain rate (when >= 30 min active usage observed)
    ///  - Realistic polling-rate hardware prior (confidence-blended to prevent wild spikes)
    ///  - Live Li-Ion voltage refinement within coarse firmware % buckets
    /// </summary>
    public static DischargeEstimate EstimateDischarge(
        int currentPercent,
        MouseProfile activeProfile,
        IReadOnlyList<BatteryHistoryPoint>? history,
        int? voltageMv = null)
    {
        int clamped = Math.Clamp(currentPercent, 0, 100);
        if (clamped == 0)
            return new DischargeEstimate(TimeSpan.Zero, null, false);

        double effectivePct = ComputeEffectiveDischargePercent(clamped, voltageMv);
        double priorFullHours = activeProfile.EstimateFullChargeActiveHours();
        double priorDrainPerHour = 100.0 / Math.Max(1.0, priorFullHours);

        var (measuredRate, activeHours, totalDrop) = AnalyzeDischargeSegment(history);
        double finalDrainPerHour;
        bool isCalibrated = false;

        if (measuredRate is double mRate && mRate >= 0.2)
        {
            // Confidence increases smoothly with active duration (up to 2h) and observed drop (up to 6%)
            double wTime = Math.Clamp(activeHours / 2.0, 0.25, 1.0);
            double wDrop = Math.Clamp(totalDrop / 6.0, 0.35, 1.0);
            double confidence = Math.Clamp(wTime * wDrop, 0.20, 1.0);

            finalDrainPerHour = (confidence * mRate) + ((1.0 - confidence) * priorDrainPerHour);
            isCalibrated = true;
        }
        else
        {
            finalDrainPerHour = priorDrainPerHour;
        }

        double hours = Math.Clamp(effectivePct / Math.Max(0.2, finalDrainPerHour), 0.1, 240.0);
        return new DischargeEstimate(TimeSpan.FromHours(hours), measuredRate, isCalibrated);
    }

    /// <summary>
    /// Backwards-compatible scalar helper used by existing callers and tests.
    /// </summary>
    public static double EstimateRemainingHours(int currentPercent, MouseProfile activeProfile, double? measuredDrainPerHour)
    {
        int pct = Math.Clamp(currentPercent, 0, 100);
        if (pct == 0) return 0;

        if (measuredDrainPerHour is double drain and >= 0.2)
            return Math.Clamp(pct / drain, 0.25, 240.0);

        return activeProfile.EstimateRemainingHours(pct);
    }

    /// <summary>
    /// Computes accurate Li-Ion CC-CV remaining charging time to 100%, blending live measured
    /// charging velocity (in CC-equivalent units/min) with the USB mouse CC-CV prior.
    /// </summary>
    public static ChargingEstimate EstimateCharging(
        int currentPercent,
        IReadOnlyList<BatteryHistoryPoint>? history)
    {
        int clamped = Math.Clamp(currentPercent, 0, 100);
        if (clamped >= 100)
            return new ChargingEstimate(TimeSpan.Zero, null, IsCalibrated: true);

        double remainingCcUnits = ToEquivalentCcPercent(100.0) - ToEquivalentCcPercent(clamped);
        var (measuredCcRate, rawPctPerMin, elapsedMin, gainPct) = AnalyzeChargingSegment(history);

        double finalCcRate;
        bool isCalibrated = false;

        if (measuredCcRate is double mCcRate && mCcRate >= 0.15)
        {
            double wTime = Math.Clamp(elapsedMin / 6.0, 0.25, 1.0);
            double wGain = Math.Clamp(gainPct / 5.0, 0.30, 1.0);
            double confidence = Math.Clamp(wTime * wGain, 0.20, 1.0);

            finalCcRate = (confidence * mCcRate) + ((1.0 - confidence) * PriorChargingCcUnitsPerMinute);
            isCalibrated = true;
        }
        else
        {
            finalCcRate = PriorChargingCcUnitsPerMinute;
        }

        double remainingMin = Math.Clamp(remainingCcUnits / Math.Max(0.15, finalCcRate), 1.0, 360.0);
        return new ChargingEstimate(TimeSpan.FromMinutes(remainingMin), rawPctPerMin, isCalibrated);
    }

    /// <summary>
    /// Formats remaining discharge or charging durations cleanly in the active language
    /// (EN: "~12 min", "~1h 15m", "~14h" | RU: "~12 мин", "~1 ч 15 мин", "~14 ч").
    /// </summary>
    public static string FormatDuration(TimeSpan duration) =>
        FormatDuration(duration, AppLocalization.CurrentLanguage);

    public static string FormatDuration(TimeSpan duration, AppLanguage language)
    {
        bool ru = language == AppLanguage.Russian;
        string minUnit = ru ? "мин" : "min";
        string hourUnit = ru ? " ч" : "h";

        double totalMin = Math.Max(0.0, duration.TotalMinutes);
        if (totalMin < 1.5)
            return $"~1 {minUnit}";

        if (totalMin < 15.0)
            return $"~{(int)Math.Round(totalMin)} {minUnit}";

        if (totalMin < 60.0)
        {
            int rounded5 = (int)(Math.Round(totalMin / 5.0) * 5);
            if (rounded5 >= 60) return $"~1{hourUnit}";
            return $"~{rounded5} {minUnit}";
        }

        double totalHours = totalMin / 60.0;
        if (totalHours >= 10.0)
            return $"~{(int)Math.Round(totalHours)}{hourUnit}";

        int roundedTotalMin = (int)(Math.Round(totalMin / 5.0) * 5);
        int h = roundedTotalMin / 60;
        int m = roundedTotalMin % 60;
        if (m == 0)
            return $"~{h}{hourUnit}";

        return ru ? $"~{h} ч {m} мин" : $"~{h}h {m}m";
    }

    public static string FormatRussianDuration(TimeSpan duration) =>
        FormatDuration(duration, AppLanguage.Russian);

    private static (double? DrainPerHour, double ActiveHours, double TotalDrop) AnalyzeDischargeSegment(
        IReadOnlyList<BatteryHistoryPoint>? history)
    {
        if (history is null || history.Count < 2)
            return (null, 0, 0);

        int endIdx = history.Count - 1;
        if (history[endIdx].IsCharging)
            return (null, 0, 0);

        // Collect the latest continuous non-charging segment with monotonic non-increasing charge
        var seg = new List<BatteryHistoryPoint> { history[endIdx] };
        for (int i = endIdx - 1; i >= 0; i--)
        {
            var prev = history[i];
            var next = seg[^1];
            if (prev.IsCharging)
                break;

            // Significant jump upward means unplugged after charge or sensor reset -> stop segment
            if (prev.Percent < next.Percent - 1)
                break;

            seg.Add(prev);
        }

        seg.Reverse();
        if (seg.Count < 2)
            return (null, 0, 0);

        // Build active-time axis (skip any gap > 25 min where PC was off or mouse was sleeping!)
        var timesHours = new List<double>(seg.Count) { 0.0 };
        var valuesPct = new List<double>(seg.Count) { ComputeEffectiveDischargePercent(seg[0].Percent, seg[0].VoltageMv) };

        double activeH = 0.0;
        for (int i = 1; i < seg.Count; i++)
        {
            var dt = seg[i].TimestampUtc - seg[i - 1].TimestampUtc;
            if (dt <= TimeSpan.Zero)
                continue;

            if (dt <= MaxContinuousDischargeGap)
            {
                activeH += dt.TotalHours;
            }
            else if (seg[i].Percent < seg[i - 1].Percent)
            {
                // Long gap WITH drop (e.g. sparse 1-hour polling or resumed session): cap gap contribution
                // only when total segment has multiple sparse points; otherwise ignore sleep gap
                if (seg.Count <= 6 && dt <= TimeSpan.FromHours(2))
                    activeH += dt.TotalHours;
                else
                    continue;
            }
            else
            {
                // Long gap with identical % -> mouse/PC was sleeping, do NOT add idle hours!
                continue;
            }

            timesHours.Add(activeH);
            valuesPct.Add(ComputeEffectiveDischargePercent(seg[i].Percent, seg[i].VoltageMv));
        }

        if (timesHours.Count < 2 || activeH < 0.45)
            return (null, activeH, 0);

        double drop = valuesPct[0] - valuesPct[^1];
        if (drop < 2.0)
            return (null, activeH, drop);

        // Ordinary least squares slope (-dPct/dHour) suppresses 5%-step quantization noise
        double meanT = timesHours.Average();
        double meanP = valuesPct.Average();
        double num = 0.0;
        double den = 0.0;
        for (int i = 0; i < timesHours.Count; i++)
        {
            double dt = timesHours[i] - meanT;
            num += dt * (valuesPct[i] - meanP);
            den += dt * dt;
        }

        double rate = den > 1e-6 ? -(num / den) : (drop / activeH);
        if (rate < 0.2)
            return (null, activeH, drop);

        return (Math.Clamp(rate, 0.25, 25.0), activeH, drop);
    }

    private static (double? CcRatePerMin, double? RawPctPerMin, double ElapsedMin, int GainPct) AnalyzeChargingSegment(
        IReadOnlyList<BatteryHistoryPoint>? history)
    {
        if (history is null || history.Count < 2)
            return (null, null, 0, 0);

        int endIdx = history.Count - 1;
        if (!history[endIdx].IsCharging)
            return (null, null, 0, 0);

        var seg = new List<BatteryHistoryPoint> { history[endIdx] };
        for (int i = endIdx - 1; i >= 0; i--)
        {
            var prev = history[i];
            var next = seg[^1];
            if (!prev.IsCharging)
                break;

            var gap = next.TimestampUtc - prev.TimestampUtc;
            if (gap > MaxContinuousChargingGap || prev.Percent > next.Percent + 1)
                break;

            seg.Add(prev);
        }

        seg.Reverse();
        if (seg.Count < 2)
            return (null, null, 0, 0);

        var first = seg[0];
        var last = seg[^1];
        double elapsedMin = (last.TimestampUtc - first.TimestampUtc).TotalMinutes;
        int gainPct = last.Percent - first.Percent;

        if (elapsedMin < 2.0 || gainPct < 2)
            return (null, null, elapsedMin, gainPct);

        double ccGain = ToEquivalentCcPercent(last.Percent) - ToEquivalentCcPercent(first.Percent);
        if (ccGain <= 0.5)
            return (null, null, elapsedMin, gainPct);

        double ccRate = Math.Clamp(ccGain / elapsedMin, 0.20, 5.0);
        double rawRate = Math.Clamp(gainPct / elapsedMin, 0.15, 5.0);
        return (ccRate, rawRate, elapsedMin, gainPct);
    }
}
