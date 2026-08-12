namespace ChargeKeeper.Vendors;

/// <summary>
/// Current battery charge-threshold configuration, as reported by the vendor's power manager.
/// <see cref="Enabled"/> is false when the battery charges to 100% (no threshold).
/// </summary>
public sealed record ChargeThresholdState(bool Capable, bool Enabled, int Start, int Stop);

/// <summary>
/// Reads and writes the battery charge start/stop thresholds through a vendor-specific
/// mechanism. Availability is signalled by <see cref="Read"/> returning <c>null</c> (driver
/// missing, unsupported hardware, transport error) rather than by a separate probe, so callers
/// have exactly one "is this working" code path.
/// </summary>
public interface IChargeThresholdProvider
{
    /// <summary>
    /// Whether the vendor can honour arbitrary start/stop percentages.
    ///
    /// Lenovo can: its firmware takes a real numeric pair. HP cannot — it exposes three coarse
    /// named modes and no numeric threshold at all, so <see cref="SetThresholds"/> there snaps
    /// to the nearest mode and the <see cref="ChargeThresholdState.Start"/>/
    /// <see cref="ChargeThresholdState.Stop"/> it reports back are nominal labels rather than
    /// firmware-reported values.
    ///
    /// The UI must consult this before offering a percentage picker; otherwise the user drags a
    /// slider to 60% and the device quietly settles somewhere else.
    /// </summary>
    bool SupportsNumericThresholds { get; }

    /// <summary>The current threshold state, or <c>null</c> if the interface is unavailable.</summary>
    ChargeThresholdState? Read();

    /// <summary>
    /// Enables the charge threshold (preserving any existing custom range, else applying the
    /// vendor's sensible defaults) or disables it so the battery charges to 100%.
    /// </summary>
    bool SetEnabled(bool enable);

    /// <summary>
    /// Writes explicit start/stop thresholds (1–100, start &lt; stop). Returns <c>false</c>
    /// without touching the device when the arguments are out of range.
    /// </summary>
    bool SetThresholds(int start, int stop);
}
