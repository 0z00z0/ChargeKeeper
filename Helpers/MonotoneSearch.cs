namespace ChargeKeeper.Helpers;

/// <summary>
/// Pure lookups over a monotonically non-decreasing list of doubles — extracted here (rather than
/// left inline in <c>BatteryHistoryGraphControl</c>) so it stays free of any XAML/WinRT dependency
/// and can be unit-tested as plain C#, like the other graph-support helpers.
/// </summary>
internal static class MonotoneSearch
{
    /// <summary>
    /// Index of the element in <paramref name="values"/> nearest to <paramref name="target"/>,
    /// assuming <paramref name="values"/> is sorted non-decreasing (as the graph's compressed-X
    /// coordinates are by construction). O(log n) binary search instead of an O(n) linear scan —
    /// the hover crosshair calls this dozens of times a second on a list downsampled to ~2× the
    /// plot width. A tie between the two straddling candidates resolves to the lower index; among a
    /// run of exactly-equal values (equal compressed-X, i.e. samples sharing a pixel column) which
    /// of them is returned is unspecified — every one is equally near, and the crosshair looks the
    /// same at that x. Returns -1 for an empty list.
    /// </summary>
    internal static int NearestIndex(IReadOnlyList<double> values, double target)
    {
        int count = values.Count;
        if (count == 0) return -1;
        if (count == 1) return 0;

        // Binary search for the first index whose value is >= target (the classic lower-bound).
        int lo = 0, hi = count - 1;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (values[mid] < target) lo = mid + 1;
            else                      hi = mid;
        }

        // lo is now the first index >= target (or the last index if every value is < target). The
        // nearest is either that element or its predecessor; pick the closer, lower index on a tie.
        int upper = lo;
        int lower = lo > 0 ? lo - 1 : 0;
        return Math.Abs(values[lower] - target) <= Math.Abs(values[upper] - target) ? lower : upper;
    }
}
