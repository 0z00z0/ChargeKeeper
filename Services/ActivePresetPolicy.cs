using ChargeKeeper.Vendors;

namespace ChargeKeeper.Services;

/// <summary>Which preset the firmware's thresholds currently correspond to. Derived from the device
/// rather than read from a stored name, so a threshold written from Settings, a network rule, MQTT
/// or a hand-edited file resolves the same way on every surface. Pure — no UI types, no vendor RPC.</summary>
internal static class ActivePresetPolicy
{
    /// <summary>The first preset whose start and stop both equal the current thresholds, else null.
    /// First match wins, so two presets carrying identical values resolve deterministically.</summary>
    public static ThresholdPreset? Match(IReadOnlyList<ThresholdPreset>? presets, ChargeThresholdState? state)
    {
        // A state that is not limiting matches nothing. Smart Charge off and an active travel
        // override both leave values on the device that no preset asked for, and a mode-based
        // vendor reports no start threshold at all.
        if (presets is null || state is null || !state.IsLimiting) return null;

        foreach (var preset in presets)
            if (preset.Start == state.Start && preset.Stop == state.Stop) return preset;

        return null;
    }
}
