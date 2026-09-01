namespace ChargeKeeper.Helpers;

/// <summary>
/// Chooses which points of a quantised series are worth interpolating through.
///
/// Battery level is reported in whole percentage points, so a series is a staircase of flat runs
/// joined by single-step jumps. An interpolation that must pass through every point and must not
/// leave the range spanned by its neighbours has no freedom inside a flat run: two equal values
/// force the curve flat between them. The staircase is therefore a property of the knot set, not of
/// the curve, and the only place it can be addressed is here.
///
/// One knot per level — the plateau's centre — spreads each step across the time the level actually
/// held. Every knot is a point taken unchanged from the input, and the distinct values are all
/// preserved, so the extremes survive and the curve still spans exactly the recorded range.
/// </summary>
internal static class PlateauKnots
{
    /// <summary>
    /// Positions in <paramref name="values"/> to keep, strictly increasing: the first and last, the
    /// centre of every maximal run of equal values, and every position for which
    /// <paramref name="forceKeep"/> is true. A series with no repeats keeps everything.
    /// </summary>
    /// <param name="forceKeep">Positions that must survive whatever the level does — a point whose
    /// colour or meaning differs from its predecessor's, which thinning would otherwise erase.</param>
    internal static int[] Select(IReadOnlyList<double> values, Func<int, bool>? forceKeep = null)
    {
        ArgumentNullException.ThrowIfNull(values);

        int n = values.Count;
        if (n <= 2) return Enumerable.Range(0, n).ToArray();

        var keep = new SortedSet<int> { 0, n - 1 };

        int runStart = 0;
        for (int i = 1; i <= n; i++)
        {
            // A run ends at the first differing value, and the last run ends with the series.
            if (i < n && values[i] == values[runStart]) continue;
            keep.Add(runStart + (i - 1 - runStart) / 2);
            runStart = i;
        }

        if (forceKeep is not null)
            for (int i = 0; i < n; i++)
                if (forceKeep(i)) keep.Add(i);

        return [.. keep];
    }
}
