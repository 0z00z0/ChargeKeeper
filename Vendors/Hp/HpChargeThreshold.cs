namespace ChargeKeeper.Vendors.Hp;

/// <summary>
/// Battery charge limiting on HP commercial hardware, via the "Battery Health Manager" BIOS
/// setting in <c>root\HP\InstrumentedBIOS</c>. HP has no numeric threshold, only three coarse
/// named modes, so <see cref="ChargeThresholdState.Start"/> is always 0 and
/// <see cref="ChargeThresholdState.Stop"/> is derived from the selected mode rather than reported
/// by the firmware. Two firmware quirks bite here: HP applies battery BIOS settings only on
/// reboot, and the separate read-only "Adaptive Battery Optimizer" setting can override the mode
/// chosen here.
/// </summary>
internal sealed class HpChargeThreshold : IChargeThresholdProvider
{
    /// <summary>The BIOS setting name, exactly as HP's firmware spells it.</summary>
    private const string SettingName = "Battery Health Manager";

    // Spelling must match the firmware's PossibleValues exactly; HP rejects anything else.
    private const string ModeMaximize = "Maximize Battery Health Management";
    private const string ModeAdaptive = "Let HP Manage My Battery Health";
    private const string ModeMinimize = "Minimize Battery Health Management";

    /// <summary>
    /// The three modes in HP Power Manager's own order, most protective first. The middle one has
    /// no on/off equivalent, which is why <see cref="SetMode"/> exists alongside
    /// <see cref="SetEnabled"/>.
    /// </summary>
    private static readonly ChargeMode[] Modes =
    [
        new(ModeMaximize, "Maximise battery health",
            "Caps the charge to protect the battery. Reduces how long a full charge lasts."),
        new(ModeAdaptive, "Let HP manage it",
            "HP's firmware decides when to cap, based on how the laptop is used."),
        new(ModeMinimize, "Charge to 100 %",
            "No limit. Longest runtime per charge, at the cost of battery ageing."),
    ];

    /// <summary>
    /// The cap "Maximize Battery Health Management" nominally applies. The firmware never reports
    /// a number, so this is a label for the UI, not a measurement.
    /// </summary>
    private const int NominalCapPercent = 80;

    /// <summary>
    /// A requested stop at or above this means "don't limit at all"; below it, any value maps to
    /// Maximize, because HP cannot honour anything finer.
    /// </summary>
    private const int TreatAsUnlimitedPercent = 95;

    /// <summary>False: HP has three named modes and no numeric threshold in its BIOS surface.</summary>
    public bool SupportsNumericThresholds => false;

    public IReadOnlyList<ChargeMode> AvailableModes => Modes;

    /// <summary>
    /// The firmware's current mode, or null when unavailable or when it reports a value this build
    /// does not list.
    /// </summary>
    public string? ReadMode()
    {
        var setting = HpBios.ReadEnumSetting(SettingName);
        if (setting is null) return null;

        return Modes.FirstOrDefault(m =>
            string.Equals(m.Id, setting.CurrentValue, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    /// <summary>Writes a mode by id. Unknown ids are rejected without contacting the firmware.</summary>
    public bool SetMode(string id)
    {
        var mode = Modes.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
        return mode is not null && HpBios.SetSetting(SettingName, mode.Id);
    }

    public ChargeThresholdState? Read()
    {
        // null propagates "unavailable": no HP BIOS WMI namespace, or no such setting.
        var setting = HpBios.ReadEnumSetting(SettingName);
        if (setting is null) return null;

        return MapState(setting.CurrentValue, setting.IsReadOnly);
    }

    /// <summary>
    /// Turns a raw BIOS mode string into the vendor-neutral state. Split out from
    /// <see cref="Read"/> so the mapping is testable without HP hardware.
    /// </summary>
    internal static ChargeThresholdState MapState(string currentValue, bool isReadOnly)
    {
        // Minimize is "charge to 100 %"; every other mode limits, including the adaptive one.
        bool enabled = !string.Equals(currentValue, ModeMinimize, StringComparison.OrdinalIgnoreCase);

        // Reachable but not writable is a real HP state, distinct from unavailable.
        bool capable = !isReadOnly;

        return new ChargeThresholdState(capable, enabled, Start: 0, Stop: enabled ? NominalCapPercent : 100);
    }

    public bool SetEnabled(bool enable)
        => HpBios.SetSetting(SettingName, enable ? ModeMaximize : ModeMinimize);

    /// <summary>
    /// Maps a numeric request onto HP's three modes by snapping to the nearest. The exact values
    /// cannot be honoured, so callers should re-<see cref="Read"/> afterwards.
    /// </summary>
    public bool SetThresholds(int start, int stop)
        => TryMapToLimiting(start, stop, out bool enableLimiting) && SetEnabled(enableLimiting);

    /// <summary>
    /// Pure decision half of <see cref="SetThresholds"/>, split out so the range guards and the
    /// snapping rule can be tested without writing to real firmware. Returns false, without any
    /// firmware contact, for a request no vendor would accept.
    /// </summary>
    internal static bool TryMapToLimiting(int start, int stop, out bool enableLimiting)
    {
        enableLimiting = false;

        // HP reports Start as 0, so 0 must be a legal input here where Lenovo requires >= 1.
        if (start < 0 || stop > 100 || start >= stop) return false;

        enableLimiting = stop < TreatAsUnlimitedPercent;
        return true;
    }
}
