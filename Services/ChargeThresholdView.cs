using ChargeKeeper.Vendors;

namespace ChargeKeeper.Services;

/// <summary>
/// The charge-threshold state to show and publish, as against the one the firmware reports.
/// </summary>
/// <remarks>
/// Lifting the cap for "charge to 100 % once" means disabling it at the firmware — on a mode-based
/// vendor that is the only lever there is — so the device reads as not limiting for the length of
/// one charge. The parked pair is the setting that was actually chosen and the one that comes back,
/// so that is what a reader is told; otherwise a one-charge lift reads as Smart Charge switched off
/// for good.
///
/// Display and publication only. A write starts from the device's own reading, never from this:
/// writing what is shown back to the firmware would cancel the lift and apply the parked pair while
/// the battery is still filling.
/// </remarks>
internal static class ChargeThresholdView
{
    /// <summary>The state to show, against the lift currently recorded.</summary>
    public static ChargeThresholdState? Shown(ChargeThresholdState? live) =>
        Shown(live, TravelOverrideService.ParkedThresholds);

    /// <summary>The decision itself, taking the parked pair rather than reading it, so it is
    /// exercised without a settings document.</summary>
    internal static ChargeThresholdState? Shown(ChargeThresholdState? live, (int Start, int Stop)? parked) =>
        live is not null && parked is { } pair
            ? live with { Enabled = true, Start = pair.Start, Stop = pair.Stop }
            : live;
}
