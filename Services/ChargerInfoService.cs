namespace ChargeKeeper.Services;

/// <summary>
/// Static facade over the active vendor's <see cref="Vendors.IChargerInfoProvider"/>
/// (see <see cref="VendorCatalog"/>), mirroring <see cref="ChargeThresholdService"/>.
/// </summary>
internal static class ChargerInfoService
{
    // The rated wattage can't change while the same adapter stays attached, but every provider read
    // is a full ncalrpc connect→call→disconnect (see native/lenpower.c) — and consumers poll it
    // (tooltip on each battery report, the pop-out's 5s stats tick). Memoize the reading here so the
    // caching policy lives in the facade instead of each caller keeping a private copy. Tri-state:
    //   > 0  cached wattage
    //   = 0  uncached — query on next read
    //   < 0  known-unavailable this AC session — the provider returned null/0 (non-capable driver or
    //        a missing vendor service); DON'T re-RPC on every event/tick forever, just retry next
    //        AC session (a non-Lenovo laptop would otherwise connect→fail→disconnect endlessly).
    // App calls Invalidate() on the AC→battery transition AND on resume, so a swapped adapter is
    // re-read. Plain ints (no lock) suit this app's low-contention access; the _generation guard
    // stops an RPC that began before an Invalidate() from repopulating the cache for a now-detached
    // adapter after the fact.
    private static int _cachedWatts;
    private static int _generation;

    internal static int? GetRatedWattage()
    {
        int cached = _cachedWatts;
        if (cached > 0) return cached;
        if (cached < 0) return null;   // known unavailable this session — no RPC

        int gen = _generation;
        int? value = VendorCatalog.Active.ChargerInfo.GetRatedWattage();
        // Only publish if no Invalidate() happened during the RPC; otherwise this reading belongs to
        // an adapter that's already been unplugged and must not seed the cache for the next one.
        if (_generation == gen)
            _cachedWatts = value is > 0 ? value.Value : -1;
        return value;
    }

    /// <summary>
    /// The memoized reading only — never triggers an RPC, so it's safe on the UI thread. Null when
    /// cold OR known-unavailable; callers that need a real read then call
    /// <see cref="GetRatedWattage"/> off-thread.
    /// </summary>
    internal static int? CachedWattage { get { int c = _cachedWatts; return c > 0 ? c : null; } }

    /// <summary>Drops the cached reading so the next read re-queries — called on AC→battery and on resume.</summary>
    internal static void Invalidate() { _generation++; _cachedWatts = 0; }
}
