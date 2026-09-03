namespace ChargeKeeper.Services;

/// <summary>
/// The one thermal reading the application currently offers, and the only thing the MQTT catalog and
/// the history tick touch to get it. Mirrors <see cref="ChargerInfoService"/>'s shape — a static
/// facade over a memoised hardware read — but adds the plausibility gate issue #157 requires: a
/// reading only ever comes back from <see cref="PublishableCelsius"/> once it has been shown to
/// exist, sit in a plausible range and actually move. See <see cref="ThermalReadingGate"/> for the
/// rules and <see cref="ThermalZoneReader"/> for where the numbers come from.
/// </summary>
internal static class ThermalStatusService
{
    private static readonly ThermalReadingGate Gate = new();
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
        bool publish = Gate.Observe(reading);
        lock (Sync) _publishableCelsius = publish ? reading : null;
    }

    /// <summary>The current temperature in Celsius, or null while the gate withholds it — no source
    /// on this machine, an implausible reading, or not yet shown to vary. Never throws.</summary>
    public static double? PublishableCelsius { get { lock (Sync) return _publishableCelsius; } }

    /// <summary>The firmware's own recommended ceiling, or null when it cannot be read or when the
    /// temperature itself is not currently publishable. Never invented, and never offered on its own:
    /// a maximum without the reading it bounds means nothing.</summary>
    public static double? RecommendedMaximumCelsius =>
        PublishableCelsius is null ? null : ThermalZoneReader.RecommendedMaximumCelsius();
}
