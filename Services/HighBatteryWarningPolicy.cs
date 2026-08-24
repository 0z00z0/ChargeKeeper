using ChargeKeeper.Vendors;

namespace ChargeKeeper.Services;

/// <summary>Pure decision for the high-battery warning: the current level, the configured
/// threshold, the enabled flag, the fire-once latch and the firmware's charge-threshold state in;
/// warn or not out. No UI types, no clock, no device reads.</summary>
internal static class HighBatteryWarningPolicy
{
    /// <summary>
    /// Fires on the upward crossing only. <paramref name="alreadyWarned"/> holds it silent for as
    /// long as the machine sits on charge; <see cref="ClearsLatch"/> re-arms it.
    /// </summary>
    public static bool ShouldWarn(bool enabled, int levelPercent, int warnAtPercent, bool alreadyWarned,
                                  ChargeThresholdState? chargeThreshold)
    {
        if (!enabled || alreadyWarned) return false;
        if (levelPercent < warnAtPercent) return false;
        if (CapIsHolding(chargeThreshold, levelPercent)) return false;
        return true;
    }

    /// <summary>Whether the level has fallen back below the threshold, re-arming the warning. No
    /// hysteresis: the warning is about exceeding a ceiling, so the ceiling is the only edge.</summary>
    public static bool ClearsLatch(int levelPercent, int warnAtPercent) => levelPercent < warnAtPercent;

    /// <summary>
    /// Smart Charge is limiting and the level is still within its cap, so the cap is doing its job
    /// and there is nothing to report. What remains — a level above the stop threshold, or no cap
    /// at all — is the case worth reporting: the battery went higher than the cap allows.
    /// </summary>
    private static bool CapIsHolding(ChargeThresholdState? chargeThreshold, int levelPercent)
        => chargeThreshold is { IsLimiting: true } state && levelPercent <= state.Stop;
}
