namespace ChargeKeeper.Helpers;

/// <summary>Pure lookups over a monotonically non-decreasing list of doubles.</summary>
internal static class MonotoneSearch
{
    /// <summary>
    /// Index of the element in <paramref name="values"/> nearest to <paramref name="target"/>,
    /// assuming <paramref name="values"/> is sorted non-decreasing. A tie between the two straddling
    /// candidates resolves to the lower index; within a run of exactly-equal values, which one comes
    /// back is unspecified. Returns -1 for an empty list.
    /// </summary>
    internal static int NearestIndex(IReadOnlyList<double> values, double target)
    {
        int count = values.Count;
        if (count == 0) return -1;
        if (count == 1) return 0;

        // Lower bound: the first index whose value is >= target.
        int lo = 0, hi = count - 1;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (values[mid] < target) lo = mid + 1;
            else                      hi = mid;
        }

        // The nearest is that element or its predecessor; the lower index wins a tie.
        int upper = lo;
        int lower = lo > 0 ? lo - 1 : 0;
        return Math.Abs(values[lower] - target) <= Math.Abs(values[upper] - target) ? lower : upper;
    }
}
