namespace ChargeKeeper.Services;

/// <summary>
/// Bounds for the numeric settings the Settings window offers as fixed dropdowns. A dropdown cannot
/// produce an out-of-range value, so it needs no validator of its own; a remote write can, and must
/// be refused by the same limits rather than by a looser second set. The values are the extremes of
/// the corresponding Settings dropdown, so a remote write can reach nothing the UI cannot.
/// </summary>
/// <remarks>Thresholds live in <see cref="PresetEditValidator"/> and the lid-close delay in
/// <see cref="LidDelayPolicy"/>; only the settings with no existing bounds are listed here.</remarks>
internal static class SettingRanges
{
    public const int LowBatteryMin  = 5;
    public const int LowBatteryMax  = 50;

    public const int HighBatteryMin = 60;
    public const int HighBatteryMax = 95;

    public const int DrainRateMin   = 1;
    public const int DrainRateMax   = 10;

    /// <summary>Zero is "no delay", not an invalid value.</summary>
    public const int StartupDelayMin = 0;
    public const int StartupDelayMax = 60;

    /// <summary>Zero is "never draw an axis break", not zero minutes.</summary>
    public const int DowntimeGapMin = 0;
    public const int DowntimeGapMax = 60;

    /// <summary>Null when in range, else the reason, so a caller logs why a write was refused.</summary>
    public static string? Validate(int value, int min, int max, string what) =>
        value >= min && value <= max ? null : $"{what} must be between {min} and {max}.";
}
