using Windows.System.Power;

namespace ChargeKeeper.Services;

/// <summary>What one battery reading means for a travel override in force.</summary>
internal enum TravelOverrideStep
{
    /// <summary>The lift stands.</summary>
    Hold,

    /// <summary>The charge the lift was asked for has begun. Recorded, so removing the charger can
    /// end the lift from then on.</summary>
    RecordChargeStarted,

    /// <summary>Put the parked thresholds back.</summary>
    Revert,
}

/// <summary>The one reading of when "charge to 100 % once" is over. Pure, so every ending is decided
/// in one place and exercised without a battery or a device.</summary>
internal static class TravelOverridePolicy
{
    /// <param name="chargeStarted">Whether the machine has been charging under this lift. Persisted
    /// rather than held in memory: a lift armed on battery must still be waiting for its charger
    /// after a restart, and one whose charge had begun must still end when the charger is gone.</param>
    /// <param name="lastStatus">The previous reading, or <see cref="BatteryStatus.NotPresent"/> in a
    /// process that has not seen one yet.</param>
    public static TravelOverrideStep Decide(
        bool active, bool chargeStarted, int pct, BatteryStatus status, BatteryStatus lastStatus)
    {
        if (!active) return TravelOverrideStep.Hold;

        // Charging completed. The Charging→Idle edge catches a worn pack settling below 100 %; Idle
        // at 100 % catches firmware that never reports a Charging phase. Each alone misses one case.
        if ((lastStatus == BatteryStatus.Charging && status == BatteryStatus.Idle) ||
            (status == BatteryStatus.Idle && pct >= 100))
            return TravelOverrideStep.Revert;

        // The charger came out before the battery filled. Without this ending the lift outlives the
        // charge it was asked for, and every later charge runs to 100 % as well.
        if (chargeStarted && status == BatteryStatus.Discharging)
            return TravelOverrideStep.Revert;

        // Armed while unplugged, which is the travel case the feature is named for: the lift waits
        // for the charger instead of ending at the first reading.
        if (!chargeStarted && status == BatteryStatus.Charging)
            return TravelOverrideStep.RecordChargeStarted;

        return TravelOverrideStep.Hold;
    }

    /// <summary>The one wording for a lift in force, so the tray menu and the dashboard cannot drift
    /// apart. Both readings stay inside the 110-character hover cap.</summary>
    public static string Describe(bool chargeStarted) =>
        chargeStarted
            ? "Charging to 100 % once — the limit comes back at full or when unplugged"
            : "Charge to 100 % once — the limit comes back after that charge";
}
