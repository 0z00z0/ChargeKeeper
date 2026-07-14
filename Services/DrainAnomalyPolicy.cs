namespace ChargeKeeper.Services;

/// <summary>
/// Pure decision for the overnight-drain anomaly warning (TODO #26): given a detected downtime
/// gap's SoC drop + duration and the user's settings, decide whether a toast should fire. Extracted
/// from <c>App.CheckDrainAnomaly</c> so the thresholds are unit-testable without a live battery or a
/// real toast, and so the noise-floor logic lives in one named place.
/// </summary>
internal static class DrainAnomalyPolicy
{
    // A %/hour rate is only meaningful over a real drop across a real span. Without these floors a
    // 1-point SoC tick across a ~90s scheduler stall extrapolates to ~40%/hour and fires a false
    // "Modern Standby misbehaving?" alarm. They're independent of the user's %/hour threshold — that
    // sets HOW fast counts as abnormal; these set the minimum evidence before the rate is trusted at
    // all, which is what makes the feature's "overnight" framing honest.
    //
    // MinGap is a RATE-TRUST floor, layered ON TOP of — not a duplicate of — the shared
    // "is this downtime?" gate (BatteryHistoryService.DowntimeThreshold). That gate (the user's
    // graph-gap setting) already decides whether Record even reports a gap here; MinGap then requires
    // the reported gap to span long enough that a %/hour extrapolation is credible (and guards the
    // division below). So the effective anomaly gate is max(the user's downtime threshold, 15 min):
    // two genuinely different concerns, no longer three parallel copies of one "was this downtime?"
    // number.
    internal const int MinDropPercent = 5;
    internal static readonly TimeSpan MinGap = TimeSpan.FromMinutes(15);

    /// <summary>
    /// True when the gap represents a genuine, trustworthy over-threshold drain. A rise or flat
    /// reading (SoC went up / unchanged while the app was off) is not an anomaly and is filtered by
    /// the <see cref="MinDropPercent"/> floor along with too-small drops.
    /// </summary>
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
