namespace ChargeKeeper.Services;

/// <summary>
/// The one thermal reading the application currently offers, and the only thing the MQTT catalog and
/// the history tick touch to get it. Mirrors <see cref="ChargerInfoService"/>'s shape — a static
/// facade over a memoised hardware read. See <see cref="ThermalZoneReader"/> for where the numbers
/// come from.
/// </summary>
internal static class ThermalStatusService
{
    /// <summary>Bounds a laptop thermal zone cannot honestly sit outside. A reading past either end
    /// is a broken sensor or a broken read, never a very cold or very hot machine.</summary>
    internal const double MinPlausibleCelsius = -10.0;
    internal const double MaxPlausibleCelsius = 125.0;

    private static readonly Lock Sync = new();
    private static double? _publishableCelsius;

    /// <summary>
    /// Takes one reading and updates what is safe to publish. Called from the application's existing
    /// fixed-cadence history tick rather than a timer of its own, so the zone is polled at that same
    /// interval regardless of whether a battery reading has arrived yet on this run.
    /// </summary>
    public static void Sample()
    {
        double? reading = ThermalZoneReader.ReadCelsius();
        lock (Sync) _publishableCelsius = IsPlausible(reading) ? reading : null;
    }

    /// <summary>Whether a reading sits inside <see cref="MinPlausibleCelsius"/> and
    /// <see cref="MaxPlausibleCelsius"/>. A missing reading is never plausible.</summary>
    internal static bool IsPlausible(double? celsius) =>
        celsius is { } value && value >= MinPlausibleCelsius && value <= MaxPlausibleCelsius;

    /// <summary>The current temperature in Celsius, or null while no source is present or the reading
    /// is out of range. Never throws.</summary>
    public static double? PublishableCelsius { get { lock (Sync) return _publishableCelsius; } }

    /// <summary>The firmware's own recommended ceiling, or null when it cannot be read or when the
    /// temperature itself is not currently publishable. Never invented, and never offered on its own:
    /// a maximum without the reading it bounds means nothing.</summary>
    public static double? RecommendedMaximumCelsius =>
        PublishableCelsius is null ? null : ThermalZoneReader.RecommendedMaximumCelsius();
}
