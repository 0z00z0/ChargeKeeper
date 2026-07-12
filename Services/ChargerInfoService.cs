namespace ChargeKeeper.Services;

/// <summary>
/// Static facade over the active vendor's <see cref="Vendors.IChargerInfoProvider"/>
/// (see <see cref="VendorCatalog"/>), mirroring <see cref="ChargeThresholdService"/>.
/// </summary>
internal static class ChargerInfoService
{
    // The rated wattage can't change while the same adapter stays attached, but every provider read
    // is a full ncalrpc connect→call→disconnect (see native/lenpower.c) — and consumers poll it
    // (tooltip on each battery report, the pop-out's 5s stats tick). Memoize the reading here so
    // the caching policy lives in the facade instead of each caller keeping a private copy; App
    // calls Invalidate() on the AC→battery transition so the next AC session re-reads whatever
    // adapter is attached then. A plain int (0 = not cached; real readings are always > 0) keeps
    // the cross-thread access trivially atomic.
    private static int _cachedWatts;

    internal static int? GetRatedWattage()
    {
        int cached = _cachedWatts;
        if (cached > 0) return cached;

        int? value = VendorCatalog.Active.ChargerInfo.GetRatedWattage();
        if (value is > 0) _cachedWatts = value.Value;   // never cache null/0 — an RPC hiccup or
                                                        // "not capable" should retry next read
        return value;
    }

    /// <summary>
    /// The memoized reading only — never triggers an RPC, so it's safe on the UI thread. Null when
    /// cold; callers that need a real read then call <see cref="GetRatedWattage"/> off-thread.
    /// </summary>
    internal static int? CachedWattage { get { int c = _cachedWatts; return c > 0 ? c : null; } }

    /// <summary>Drops the cached reading (called on the AC→battery transition).</summary>
    internal static void Invalidate() => _cachedWatts = 0;
}
