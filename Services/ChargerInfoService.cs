namespace ChargeKeeper.Services;

/// <summary>Static facade over the active vendor's <see cref="Vendors.IChargerInfoProvider"/>
/// (see <see cref="VendorCatalog"/>), mirroring <see cref="ChargeThresholdService"/>.</summary>
internal static class ChargerInfoService
{
    // Every provider read is a full ncalrpc connect→call→disconnect and consumers poll it, so the
    // reading is memoised here. Tri-state:
    //   > 0  cached wattage
    //   = 0  uncached — query on next read
    //   < 0  known-unavailable this AC session; retry next session rather than connect→fail→disconnect
    //        forever on a machine whose driver cannot answer.
    // Volatile/Interlocked because Invalidate() runs on the SystemEvents power thread and the battery
    // MTA thread while readers sit on the UI and MQTT threads — a lost increment would let a stale
    // in-flight RPC republish the previous adapter's wattage for a whole AC session.
    private static int _cachedWatts;
    private static int _generation;

    internal static int? GetRatedWattage()
    {
        int cached = Volatile.Read(ref _cachedWatts);
        if (cached > 0) return cached;
        if (cached < 0) return null;   // known unavailable this session — no RPC

        int gen = Volatile.Read(ref _generation);
        int? value = VendorCatalog.Active.ChargerInfo.GetRatedWattage();
        // Publish only if no Invalidate() happened during the RPC — otherwise this reading belongs to
        // an adapter that has already been unplugged.
        if (Volatile.Read(ref _generation) == gen)
            Volatile.Write(ref _cachedWatts, value is > 0 ? value.Value : -1);
        return value;
    }

    /// <summary>The memoised reading only — never triggers an RPC, so it is safe on the UI thread.
    /// Null when cold or known-unavailable.</summary>
    internal static int? CachedWattage
    {
        get { int c = Volatile.Read(ref _cachedWatts); return c > 0 ? c : null; }
    }

    /// <summary>Drops the cached reading so the next read re-queries — called on AC→battery and on resume.</summary>
    internal static void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        Volatile.Write(ref _cachedWatts, 0);
    }
}
