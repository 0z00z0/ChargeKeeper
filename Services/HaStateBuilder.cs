namespace ChargeKeeper.Services;

/// <summary>
/// Pure mapping from ChargeKeeper's live battery values to an <see cref="HaState"/> for MQTT
/// publishing (TODO #28). Extracted from <c>App.OnBatteryReportUpdated</c> so the "which fields are
/// known" gating — Smart Charge thresholds only when the controller is capable AND Smart Charge is
/// on, adapter wattage only while on AC — is unit-testable without a live battery or a broker. A
/// null/omitted field renders its Home Assistant entity "unknown" rather than a fabricated 0.
/// </summary>
internal static class HaStateBuilder
{
    public static HaState Build(int soc, int chargeRateMw, bool onAc,
        ChargeThresholdState? threshold, int? adapterWatts)
    {
        int? start = threshold is { Capable: true, Enabled: true, Start: > 0 } ? threshold.Start : null;
        int? stop  = threshold is { Capable: true, Enabled: true, Stop:  > 0 } ? threshold.Stop  : null;
        // A wattage reading only belongs to the current AC session; never publish a stale one while
        // on battery (the caller already nulls it, but gate here too so the mapping is self-contained).
        int? watts = onAc ? adapterWatts : null;
        return new HaState(soc, chargeRateMw, onAc, start, stop, watts);
    }
}
