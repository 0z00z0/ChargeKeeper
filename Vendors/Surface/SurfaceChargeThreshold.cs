namespace ChargeKeeper.Vendors.Surface;

/// <summary>
/// Battery charge limiting on Surface hardware, via the "Battery Limit" UEFI setting.
///
/// IMPORTANT — Surface is NOT numeric, and is coarser than HP. Lenovo exposes a real start/stop
/// percentage pair and HP three named modes; Surface exposes ONE on/off switch. Battery Limit is
/// on or it is off, and the cap it applies is fixed in firmware — there is no percentage to pick
/// and no adaptive middle setting.
///
/// So <see cref="ChargeThresholdState.Start"/> and <see cref="ChargeThresholdState.Stop"/> are
/// NOMINAL here, exactly as in the HP module: Start is always 0 (Surface has no charge *start*
/// threshold) and Stop is <see cref="NominalCapPercent"/> when the limit is on. Callers must
/// treat a successful write as "the mode changed", then re-<see cref="Read"/>.
///
/// Three caveats that matter for anyone picking this up:
/// 1. The cap is 50 %, NOT 80 %. Microsoft documents Battery Limit as stopping at 50 % of
///    capacity, fixed and not adjustable — it is a kiosk/always-plugged-in feature, not a
///    battery-longevity slider. The ~80 % behaviour people associate with Surface is Smart
///    Charging, a separate adaptive Windows feature with no user-facing on/off this can drive.
/// 2. Like HP's BIOS settings, a UEFI write is expected to apply on REBOOT.
/// 3. Nothing here reaches hardware yet — <see cref="SurfaceBatteryLimitApi"/> ships a stub
///    transport, so <see cref="Read"/> returns null everywhere and the module is inert.
/// </summary>
internal sealed class SurfaceChargeThreshold : IChargeThresholdProvider
{
    // The two states of the Battery Limit setting, spelled as Surface UEFI spells its enum
    // values. UNVERIFIED along with the rest of the mechanism, but contained: the transport is
    // boolean, so these ids never leave this file's mapping, and above the module a ChargeMode.Id
    // is opaque by contract.
    private const string ModeLimit = "Enabled";
    private const string ModeFull = "Disabled";

    /// <summary>
    /// The two modes as the UI should present them, most protective first — same order rule as
    /// the HP module.
    ///
    /// There is no third mode to hide here, so <see cref="SetEnabled"/> can reach both. The list
    /// exists anyway because <see cref="SupportsNumericThresholds"/> is false: without it the UI
    /// would have neither a percentage picker nor a mode list and could offer nothing at all.
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
    /// A requested stop at or above this is treated as "don't limit at all". Below it, any value
    /// maps to the limit — Surface cannot honour anything finer.
    ///
    /// Same threshold as the HP module because it answers the same question ("does the user want
    /// a cap at all?"), but note the miss is far wider here: a request for 80 % lands on a 50 %
    /// cap. That is why <see cref="SupportsNumericThresholds"/> is false and callers must
    /// re-<see cref="Read"/> rather than trust what they asked for.
    /// </summary>
    private const int TreatAsUnlimitedPercent = 95;

    /// <summary>
    /// False: Battery Limit is a single on/off switch with a firmware-fixed cap. The UI must not
    /// offer a percentage picker on this hardware.
    /// </summary>
    public bool SupportsNumericThresholds => false;

    public IReadOnlyList<ChargeMode> AvailableModes => Modes;

    /// <summary>The firmware's current mode, or null when unavailable.</summary>
    public string? ReadMode()
    {
        var setting = SurfaceBatteryLimitApi.Read();
        if (setting is null) return null;

        return setting.Enabled ? ModeLimit : ModeFull;
    }

    /// <summary>
    /// Writes a mode by id. Unknown ids are rejected WITHOUT contacting the firmware, keeping the
    /// "no device contact on invalid input" guarantee the rest of this interface makes.
    /// </summary>
    public bool SetMode(string id)
    {
        var mode = Modes.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
        return mode is not null && SetEnabled(mode.Id == ModeLimit);
    }

    public ChargeThresholdState? Read()
    {
        // null propagates "unavailable" — not a Surface, or (today, on every machine) the stub
        // transport, which is what keeps this module inert until the mechanism is confirmed.
        var setting = SurfaceBatteryLimitApi.Read();
        if (setting is null) return null;

        return MapState(setting.Enabled, setting.IsReadOnly);
    }

    /// <summary>
    /// Turns the raw Battery Limit state into the vendor-neutral state. Split out from
    /// <see cref="Read"/> so the mapping is testable without Surface hardware — everything
    /// interesting about this module's read path is decided here.
    /// </summary>
    internal static ChargeThresholdState MapState(bool limitEnabled, bool isReadOnly)
    {
        // Reachable but not writable is a real Surface state: a SEMM-enrolled device exposes UEFI
        // settings its IT policy owns and refuses local changes to. That is the "reachable but not
        // capable" case the contract distinguishes from "unavailable", so report it.
        bool capable = !isReadOnly;

        return new ChargeThresholdState(
            capable, limitEnabled, Start: 0, Stop: limitEnabled ? NominalCapPercent : 100);
    }

    public bool SetEnabled(bool enable) => SurfaceBatteryLimitApi.SetEnabled(enable);

    /// <summary>
    /// Maps a numeric request onto the on/off switch. The exact values CANNOT be honoured — the
    /// firmware has no numeric threshold — so this snaps and returns whether the write succeeded.
    /// Callers should re-<see cref="Read"/> afterwards; the state it reports is the truth.
    /// </summary>
    public bool SetThresholds(int start, int stop)
        => TryMapToLimiting(start, stop, out bool enableLimiting) && SetEnabled(enableLimiting);

    /// <summary>
    /// Pure decision half of <see cref="SetThresholds"/>: validates the request and works out
    /// whether it means "limit" or "don't limit". Returns false for a request no vendor would
    /// accept, in which case NO firmware contact happens.
    ///
    /// Separated so the range guards and the snapping rule can be tested on any machine.
    /// </summary>
    internal static bool TryMapToLimiting(int start, int stop, out bool enableLimiting)
    {
        enableLimiting = false;

        // Same guard as the HP module: Surface likewise has no start threshold and reports Start
        // as 0, so 0 must be a legal input where Lenovo requires >= 1.
        if (start < 0 || stop > 100 || start >= stop) return false;

        enableLimiting = stop < TreatAsUnlimitedPercent;
        return true;
    }
}
