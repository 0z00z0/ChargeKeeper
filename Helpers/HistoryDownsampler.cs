using LenovoTray.Services;

namespace LenovoTray.Helpers;

/// <summary>
/// Reduces a large, time-ordered <see cref="BatterySample"/> list to roughly a target point count
/// before rendering, so a multi-day/week window (tens of thousands of raw samples) doesn't force
/// full-resolution processing and element allocation on every periodic render tick. Pure logic,
/// no WinUI dependency, so it's directly unit-testable.
/// </summary>
internal static class HistoryDownsampler
{
    /// <summary>
    /// <paramref name="Samples"/> is the (possibly reduced) list to render. <paramref name="GapBeforeIndices"/>
    /// holds the indices (into <see cref="Samples"/>) that are preceded by a REAL timeline gap in
    /// the original, full-resolution data — the renderer must use this, not a fresh Δt check on
    /// the reduced samples: two adjacent points that survived reduction can legitimately be far
    /// apart in time purely because of the stride, which would otherwise look like a gap.
    /// </summary>
    public readonly record struct Result(IReadOnlyList<BatterySample> Samples, IReadOnlySet<int> GapBeforeIndices);

    /// <summary>
    /// Strides through <paramref name="samples"/> picking roughly <paramref name="maxPoints"/> of
    /// them, while always preserving the first and last sample and BOTH endpoints of every real
    /// timeline gap (a Δt beyond <paramref name="gapThreshold"/> in the ORIGINAL data) — so a
    /// downtime marker or the overall time range can never be silently smoothed away by the
    /// reduction. Gap detection always runs against the original full-resolution timestamps (see
    /// <see cref="Result"/>), even when no reduction ends up being needed.
    /// </summary>
    public static Result Reduce(IReadOnlyList<BatterySample> samples, int maxPoints, TimeSpan gapThreshold)
    {
        var trueGapAfter = new HashSet<int>();   // original-index space: index i = gap between i-1 and i
        for (int i = 1; i < samples.Count; i++)
            if (samples[i].AtUtc - samples[i - 1].AtUtc > gapThreshold)
                trueGapAfter.Add(i);

        if (maxPoints <= 0 || samples.Count <= maxPoints)
            return new Result(samples, trueGapAfter);   // indices are already in the right space

        var mustKeep = new HashSet<int> { 0, samples.Count - 1 };
        foreach (int i in trueGapAfter)
        {
            mustKeep.Add(i - 1);
            mustKeep.Add(i);
        }

        var keptIndices = new SortedSet<int>(mustKeep);
        double step = (double)samples.Count / maxPoints;
        for (double idx = 0; idx < samples.Count; idx += step)
            keptIndices.Add((int)idx);

        var reduced   = new List<BatterySample>(keptIndices.Count);
        var indexMap  = new Dictionary<int, int>(keptIndices.Count);   // original index -> reduced index
        foreach (int origIdx in keptIndices)
        {
            indexMap[origIdx] = reduced.Count;
            reduced.Add(samples[origIdx]);
        }

        // Both endpoints of every true gap are always in mustKeep, so both are guaranteed present
        // in indexMap — no gap can be lost in translation.
        var reducedGapIndices = new HashSet<int>();
        foreach (int origGapIdx in trueGapAfter)
            reducedGapIndices.Add(indexMap[origGapIdx]);

        return new Result(reduced, reducedGapIndices);
    }
}
