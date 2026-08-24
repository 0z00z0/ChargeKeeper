namespace ChargeKeeper.Services;

/// <summary>Pure decision for the overnight-drain anomaly warning: given a downtime gap's SoC drop and
/// duration plus the user's settings, decide whether a toast should fire.</summary>
internal static class DrainAnomalyPolicy
{
    // Noise floors, independent of the user's %/hour threshold: without them a 1-point tick across a
    // ~90s scheduler stall extrapolates to ~40%/hour and fires a false alarm. MinGap doubles as the
    // fallback gate BatteryHistoryService.AnomalyGapThreshold uses when the graph gap is "None" — that
    // suppresses graph breaks but must not disable detection, so the gate is max(user threshold, 15 min).
    internal const int MinDropPercent = 5;
    internal static readonly TimeSpan MinGap = TimeSpan.FromMinutes(15);

    /// <summary>Rises and flat readings fall out through the <see cref="MinDropPercent"/> floor along
    /// with too-small drops.</summary>
    public static bool ShouldWarn(bool enabled, int socDropPercent, TimeSpan gapDuration, int thresholdPercentPerHour)
    {
        if (!enabled) return false;
        if (socDropPercent < MinDropPercent) return false;   // too small (also excludes rises/flats)
        if (gapDuration < MinGap) return false;              // too short to extrapolate a rate from
                                                             // (also guards the division below)

        double ratePerHour = socDropPercent / gapDuration.TotalHours;
        return ratePerHour >= thresholdPercentPerHour;
    }
}
