namespace ChargeKeeper.Services;

/// <summary>Which Smart Charge surface the current hardware warrants.</summary>
internal enum SmartChargeSurface
{
    /// <summary>No charge-limit interface at all — show a single explanatory line, nothing else.</summary>
    Hidden,

    /// <summary>The vendor's discrete BIOS modes (HP), with no percentage picker.</summary>
    FixedModes,

    /// <summary>Numeric start/stop percentages, so presets and network profiles apply (Lenovo).</summary>
    Numeric,
}

/// <summary>Pure decision for what the Smart Charge surface should offer, shared by Settings and the
/// dashboard so the two cannot drift. Unit-testable without a WinUI window or vendor hardware.</summary>
internal static class ThresholdCapabilityPolicy
{
    /// <summary>A <c>null</c> <paramref name="state"/> means the vendor interface is unavailable.</summary>
    public static SmartChargeSurface Classify(ChargeThresholdState? state, bool supportsNumeric) =>
        state is null ? SmartChargeSurface.Hidden

        // Capable:false with a readable state (HP's read-only BIOS setting) deliberately keeps the
        // surface — the hardware has the feature, so hiding it would read as a detection bug.
        : supportsNumeric ? SmartChargeSurface.Numeric
        : SmartChargeSurface.FixedModes;
}
