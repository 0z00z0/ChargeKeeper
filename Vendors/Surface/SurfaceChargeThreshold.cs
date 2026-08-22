namespace ChargeKeeper.Vendors.Surface;

/// <summary>
/// Battery charge limiting on Surface hardware, via the "Battery Limit" UEFI setting: a single
/// on/off switch with a firmware-fixed cap, no percentage to pick and no adaptive middle setting.
/// <see cref="ChargeThresholdState.Start"/> is therefore always 0 and
/// <see cref="ChargeThresholdState.Stop"/> is derived from the switch rather than reported by the
/// firmware. The module stays inert until <see cref="SurfaceBatteryLimitApi"/> gets a real
/// transport.
/// </summary>
internal sealed class SurfaceChargeThreshold : IChargeThresholdProvider
{
    // Spelled as Surface UEFI spells its enum values. Unverified, but contained: the transport is
    // boolean, so these ids never leave this file's mapping.
    private const string ModeLimit = "Enabled";
    private const string ModeFull = "Disabled";

    /// <summary>
    /// The two modes as the UI should present them, most protective first. The list exists even
    /// though <see cref="SetEnabled"/> reaches both, because with
    /// <see cref="SupportsNumericThresholds"/> false the UI has nothing else to offer.
    /// </summary>
    private static readonly ChargeMode[] Modes =
    [
        new(ModeLimit, "Limit charging",
            "Stops charging at about 50 %. Protects the battery on a device that stays plugged in."),
        new(ModeFull, "Charge to 100 %",
            "No limit. Longest runtime per charge, at the cost of battery ageing."),
    ];

    /// <summary>
    /// The cap Battery Limit nominally applies. Microsoft documents 50 % and the firmware reports
    /// no number, so this is a label for the UI, not a measurement.
    /// </summary>
    private const int NominalCapPercent = 50;

    /// <summary>
    /// A requested stop at or above this means "don't limit at all"; below it, any value maps to
    /// the limit. The miss can be wide — a request for 80 % lands on a 50 % cap — so callers must
    /// re-<see cref="Read"/> rather than trust what they asked for.
    /// </summary>
    private const int TreatAsUnlimitedPercent = 95;

    /// <summary>False: Battery Limit is one on/off switch with a firmware-fixed cap.</summary>
    public bool SupportsNumericThresholds => false;

    public IReadOnlyList<ChargeMode> AvailableModes => Modes;

    /// <summary>The firmware's current mode, or null when unavailable.</summary>
    public string? ReadMode()
    {
        var setting = SurfaceBatteryLimitApi.Read();
        if (setting is null) return null;

        return setting.Enabled ? ModeLimit : ModeFull;
    }

    /// <summary>Writes a mode by id. Unknown ids are rejected without contacting the firmware.</summary>
    public bool SetMode(string id)
    {
        var mode = Modes.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
        return mode is not null && SetEnabled(mode.Id == ModeLimit);
    }

    public ChargeThresholdState? Read()
    {
        // null propagates "unavailable": not a Surface, or — today, everywhere — the stub transport.
        var setting = SurfaceBatteryLimitApi.Read();
        if (setting is null) return null;

        return MapState(setting.Enabled, setting.IsReadOnly);
    }

    /// <summary>
    /// Turns the raw Battery Limit state into the vendor-neutral state. Split out from
    /// <see cref="Read"/> so the mapping is testable without Surface hardware.
    /// </summary>
    internal static ChargeThresholdState MapState(bool limitEnabled, bool isReadOnly)
    {
        // Reachable but not writable is a real state on a SEMM-enrolled device, whose UEFI
        // settings its IT policy owns. Distinct from unavailable, so report it.
        bool capable = !isReadOnly;

        return new ChargeThresholdState(
            capable, limitEnabled, Start: 0, Stop: limitEnabled ? NominalCapPercent : 100);
    }

    public bool SetEnabled(bool enable) => SurfaceBatteryLimitApi.SetEnabled(enable);

    /// <summary>
    /// Maps a numeric request onto the on/off switch by snapping. The exact values cannot be
    /// honoured, so callers should re-<see cref="Read"/> afterwards.
    /// </summary>
    public bool SetThresholds(int start, int stop)
        => TryMapToLimiting(start, stop, out bool enableLimiting) && SetEnabled(enableLimiting);

    /// <summary>
    /// Pure decision half of <see cref="SetThresholds"/>, split out so the range guards and the
    /// snapping rule can be tested on any machine. Returns false, without any firmware contact,
    /// for a request no vendor would accept.
    /// </summary>
    internal static bool TryMapToLimiting(int start, int stop, out bool enableLimiting)
    {
        enableLimiting = false;

        // Surface reports Start as 0, so 0 must be a legal input where Lenovo requires >= 1.
        if (start < 0 || stop > 100 || start >= stop) return false;

        enableLimiting = stop < TreatAsUnlimitedPercent;
        return true;
    }
}
