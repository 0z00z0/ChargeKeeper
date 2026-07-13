namespace ChargeKeeper.Services;

/// <summary>
/// Pure mapping from ChargeKeeper's live battery values to an <see cref="HaState"/> for MQTT
/// publishing (TODO #28). Extracted from <c>App.OnBatteryReportUpdated</c> so the "which fields are
/// known" gating is unit-testable without a live battery or a broker. Smart Charge is "on" only when
/// the controller is capable AND enabled AND both thresholds are valid; when off, Charge stop is
/// reported as 100 (charging allowed to full) while Charge start is omitted (its HA entity reads
/// "unknown/unavailable"). Adapter wattage is known only while on AC.
/// </summary>
internal static class HaStateBuilder
{
    public static HaState Build(int soc, int chargeRateMw, bool onAc,
        ChargeThresholdState? threshold, int? adapterWatts)
    {
        bool scEnabled = threshold is { Capable: true, Enabled: true, Start: > 0, Stop: > 0 };
        // Off (or the "charge to 100% once" travel override) → stop 100, start omitted. This single
        // "off" branch also covers the override because activating it calls
        // ChargeThresholdService.SetEnabled(false), so the live threshold read already comes back
        // Enabled: false while the override is active — no separate travel-override parameter needed.
        int? start = scEnabled ? threshold!.Start : null;
        int? stop  = scEnabled ? threshold!.Stop  : 100;
        // A wattage reading only belongs to the current AC session; never publish a stale one while
        // on battery (the caller already nulls it, but gate here too so the mapping is self-contained).
        int? watts = onAc ? adapterWatts : null;
        return new HaState(soc, chargeRateMw, onAc, scEnabled, start, stop, watts);
    }
}
