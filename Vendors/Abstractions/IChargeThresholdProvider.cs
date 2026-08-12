namespace ChargeKeeper.Vendors;

/// <summary>
/// Current battery charge-threshold configuration, as reported by the vendor's power manager.
/// <see cref="Enabled"/> is false when the battery charges to 100% (no threshold).
/// </summary>
public sealed record ChargeThresholdState(bool Capable, bool Enabled, int Start, int Stop);

/// <summary>
/// One discrete charge mode offered by a vendor that has no numeric threshold.
/// </summary>
/// <param name="Id">
/// The firmware's own name for the mode, passed straight back to the device on write. Opaque to
/// everything above the vendor module — never parse it, never display it.
/// </param>
/// <param name="Label">Short display name for the UI.</param>
/// <param name="Description">One line explaining what the mode does, shown under the label.</param>
public sealed record ChargeMode(string Id, string Label, string Description);

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

    /// <summary>
    /// The discrete modes this vendor offers, in the order they should be presented, or an empty
    /// list when it uses numeric thresholds instead.
    ///
    /// This exists because <see cref="SetEnabled"/> is a bool and some vendors have more than two
    /// states. HP has three — a full cap, an adaptive middle setting where the firmware decides,
    /// and off — so an on/off toggle silently hides one of them and cannot report which is
    /// selected when the user picked the middle one outside ChargeKeeper.
    ///
    /// A vendor exposes EITHER numeric thresholds OR modes, never both:
    /// <see cref="SupportsNumericThresholds"/> and a non-empty list here are mutually exclusive.
    /// </summary>
    IReadOnlyList<ChargeMode> AvailableModes { get; }

    /// <summary>
    /// The <see cref="ChargeMode.Id"/> currently selected, or <c>null</c> when unavailable or
    /// when the vendor is not mode-based. May also be null if the firmware reports a mode this
    /// build does not know about, which is why callers must handle "no selection".
    /// </summary>
    string? ReadMode();

    /// <summary>
    /// Selects a mode by its <see cref="ChargeMode.Id"/>. Returns <c>false</c> without touching
    /// the device when the id is not one of <see cref="AvailableModes"/>.
    /// </summary>
    bool SetMode(string id);
}
