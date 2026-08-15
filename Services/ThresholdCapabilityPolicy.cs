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

/// <summary>
/// Pure decision for what the Smart Charge surface should offer, shared by Settings and the
/// dashboard so the two cannot drift — the exact failure PRs #80/#81 fixed on the Settings side
/// only. Extracted so the rule is unit-testable without a live WinUI window or vendor hardware.
/// </summary>
internal static class ThresholdCapabilityPolicy
{
    /// <summary>
    /// Picks the surface from a vendor read (<c>null</c> = interface unavailable) and whether that
    /// vendor honours arbitrary percentages.
    /// </summary>
    public static SmartChargeSurface Classify(ChargeThresholdState? state, bool supportsNumeric) =>
        // No vendor answered — driver missing, unsupported hardware, transport error. There is
        // nothing to configure and no reading to show, so a permanently "Unavailable" badge is
        // dead UI.
        state is null ? SmartChargeSurface.Hidden

        // Capable:false with a READABLE state (HP's read-only BIOS setting) deliberately keeps the
        // surface: the hardware has the feature, ChargeKeeper just cannot drive it, and hiding it
        // would read as a detection bug. The caller notes the read-only part.
        : supportsNumeric ? SmartChargeSurface.Numeric
        : SmartChargeSurface.FixedModes;
}
