namespace ChargeKeeper.Vendors.Hp;

/// <summary>
/// Battery charge limiting on HP commercial hardware, via the "Battery Health Manager" BIOS
/// setting in <c>root\HP\InstrumentedBIOS</c>.
///
/// IMPORTANT — HP is NOT numeric. Lenovo exposes a real start/stop percentage pair; HP exposes
/// three coarse named modes and nothing else. Every setting on an EliteBook 840 G8 (BIOS T76
/// 01.24.02) was enumerated and there is no charge-threshold integer anywhere. The only
/// battery-percentage integer HP offers is "Disable Charging Port in sleep/off if battery
/// below (%)", which governs USB port charging during sleep, not the charge limit.
///
/// So <see cref="ChargeThresholdState.Start"/> and <see cref="ChargeThresholdState.Stop"/> are
/// NOMINAL here — derived from the selected mode, not reported by the firmware. Start is always
/// 0 (HP has no concept of a charge *start* threshold), and Stop is
/// <see cref="NominalCapPercent"/> when limiting is on. Callers must treat a successful write
/// as "the mode changed", then re-<see cref="Read"/> to see what actually applies.
///
/// Two caveats that matter for anyone debugging this:
/// 1. HP applies battery BIOS settings on REBOOT. A successful write is not yet in effect.
/// 2. "Adaptive Battery Optimizer" is a separate, READ-ONLY setting that is Activated on this
///    hardware. HP's adaptive firmware may override the mode selected here, which is the most
///    likely reason HP's own Power Manager app appears to do nothing.
/// </summary>
internal sealed class HpChargeThreshold : IChargeThresholdProvider
{
    /// <summary>The BIOS setting name, exactly as HP's firmware spells it.</summary>
    private const string SettingName = "Battery Health Manager";

    // The three modes. Spelling must match the firmware's PossibleValues exactly — HP rejects
    // anything else with a non-zero return rather than coercing it.
    private const string ModeMaximize = "Maximize Battery Health Management";
    private const string ModeMinimize = "Minimize Battery Health Management";

    /// <summary>
    /// The cap "Maximize Battery Health Management" nominally applies. HP documents this as
    /// roughly 80% but the firmware never reports a number, so this is a label for the UI, not
    /// a measurement.
    /// </summary>
    private const int NominalCapPercent = 80;

    /// <summary>
    /// A requested stop at or above this is treated as "don't limit at all" and mapped to
    /// Minimize. Below it, any value maps to Maximize — HP cannot honour anything finer.
    /// </summary>
    private const int TreatAsUnlimitedPercent = 95;

    /// <summary>
    /// False: HP has three named modes and no numeric threshold anywhere in its BIOS surface.
    /// The UI must not offer a percentage picker on this hardware.
    /// </summary>
    public bool SupportsNumericThresholds => false;

    public ChargeThresholdState? Read()
    {
        // null propagates "unavailable" — not an HP commercial machine, no HP BIOS WMI
        // namespace, or the firmware has no Battery Health Manager setting.
        var setting = HpBios.ReadEnumSetting(SettingName);
        if (setting is null) return null;

        return MapState(setting.CurrentValue, setting.IsReadOnly);
    }

    /// <summary>
    /// Turns a raw BIOS mode string into the vendor-neutral state. Split out from
    /// <see cref="Read"/> so the mapping is testable without HP hardware — everything
    /// interesting about this module's read path is decided here.
    /// </summary>
    internal static ChargeThresholdState MapState(string currentValue, bool isReadOnly)
    {
        // Minimize == charge to 100%, i.e. limiting is off. Any other mode limits in some form,
        // including "Let HP Manage My Battery Health", where HP's adaptive logic picks the point.
        bool enabled = !string.Equals(currentValue, ModeMinimize, StringComparison.OrdinalIgnoreCase);

        // Reachable but not writable is a real HP state: the firmware exposes some settings for
        // reading while refusing changes. That is exactly the "reachable but not capable" case
        // the contract distinguishes from "unavailable", so report it rather than hiding it.
        bool capable = !isReadOnly;

        return new ChargeThresholdState(capable, enabled, Start: 0, Stop: enabled ? NominalCapPercent : 100);
    }

    public bool SetEnabled(bool enable)
        => HpBios.SetSetting(SettingName, enable ? ModeMaximize : ModeMinimize);

    /// <summary>
    /// Maps a numeric request onto HP's three modes. The exact values CANNOT be honoured — the
    /// firmware has no numeric threshold — so this snaps to the nearest mode and returns whether
    /// the write succeeded. Callers should re-<see cref="Read"/> afterwards; the state it reports
    /// is the truth, not the values passed here.
    /// </summary>
    public bool SetThresholds(int start, int stop)
        => TryMapToLimiting(start, stop, out bool enableLimiting) && SetEnabled(enableLimiting);

    /// <summary>
    /// Pure decision half of <see cref="SetThresholds"/>: validates the request and works out
    /// whether it means "limit" or "don't limit". Returns false for a request no vendor would
    /// accept, in which case NO firmware contact happens — matching the interface's "returns
    /// false without touching the device when the arguments are out of range".
    ///
    /// Separated so the range guards and the snapping rule can be tested on any machine; calling
    /// <see cref="SetThresholds"/> in a unit test would write to real firmware.
    /// </summary>
    internal static bool TryMapToLimiting(int start, int stop, out bool enableLimiting)
    {
        enableLimiting = false;

        // Same guard as the Lenovo module, adjusted for HP having no start threshold — HP
        // reports Start as 0, so 0 must be a legal input here where Lenovo requires >= 1.
        if (start < 0 || stop > 100 || start >= stop) return false;

        enableLimiting = stop < TreatAsUnlimitedPercent;
        return true;
    }
}
