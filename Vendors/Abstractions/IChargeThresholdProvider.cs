namespace ChargeKeeper.Vendors;

/// <summary>
/// Current battery charge-threshold configuration, as reported by the vendor's power manager.
/// <see cref="Enabled"/> is false when the battery charges to 100% (no threshold).
/// </summary>
public sealed record ChargeThresholdState(bool Capable, bool Enabled, int Start, int Stop)
{
    /// <summary>
    /// Firmware is capping the charge. <see cref="Start"/> is deliberately not tested: HP and
    /// Surface have no charge-start threshold and report it as 0 by contract.
    /// </summary>
    public bool IsLimiting => Capable && Enabled && Stop > 0;

    /// <summary>
    /// <see cref="Start"/> is a real firmware figure rather than the 0 a mode-based vendor
    /// reports — the only condition under which a "Start-Stop" range may be shown or published.
    /// </summary>
    public bool HasStartThreshold => IsLimiting && Start > 0;
}

/// <summary>One discrete charge mode offered by a vendor that has no numeric threshold.</summary>
/// <param name="Id">
/// The firmware's own name for the mode, written back to the device verbatim. Opaque above the
/// vendor module — never parse it, never display it.
/// </param>
/// <param name="Label">Short display name for the UI.</param>
/// <param name="Description">One line explaining what the mode does, shown under the label.</param>
public sealed record ChargeMode(string Id, string Label, string Description);

/// <summary>
/// Reads and writes the battery charge start/stop thresholds through a vendor-specific
/// mechanism. <see cref="Read"/> returning <c>null</c> is how a module signals "not this
/// machine"; there is no separate availability probe.
/// </summary>
public interface IChargeThresholdProvider
{
    /// <summary>
    /// Whether the vendor can honour arbitrary start/stop percentages. A mode-based vendor snaps
    /// to its nearest mode and reports nominal labels rather than firmware figures, so the UI
    /// must consult this before offering a percentage picker.
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
    /// The discrete modes this vendor offers, in presentation order, or an empty list when it uses
    /// numeric thresholds instead. A vendor exposes either numeric thresholds or modes, never
    /// both, so this and <see cref="SupportsNumericThresholds"/> are mutually exclusive.
    /// </summary>
    IReadOnlyList<ChargeMode> AvailableModes { get; }

    /// <summary>
    /// The <see cref="ChargeMode.Id"/> currently selected, or <c>null</c> when unavailable, when
    /// the vendor is not mode-based, or when the firmware reports a mode this build does not
    /// list — so callers must handle "no selection".
    /// </summary>
    string? ReadMode();

    /// <summary>
    /// Selects a mode by its <see cref="ChargeMode.Id"/>. Returns <c>false</c> without touching
    /// the device when the id is not one of <see cref="AvailableModes"/>.
    /// </summary>
    bool SetMode(string id);
}
