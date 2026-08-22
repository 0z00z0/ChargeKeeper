using ChargeKeeper.Services;

namespace ChargeKeeper.Helpers;

/// <summary>
/// Reduces a large, time-ordered <see cref="BatterySample"/> list to roughly a target point count
/// before rendering, so a multi-week window does not force full-resolution work on every render tick.
/// </summary>
internal static class HistoryDownsampler
{
    /// <summary>
    /// <paramref name="GapBeforeIndices"/> holds the indices into <paramref name="Samples"/> preceded
    /// by a real timeline gap in the ORIGINAL data. The renderer must use this rather than a fresh Δt
    /// check on the reduced samples, where the stride alone can put two surviving points far apart.
    /// </summary>
    public readonly record struct Result(IReadOnlyList<BatterySample> Samples, IReadOnlySet<int> GapBeforeIndices);

    /// <summary>
    /// Strides through <paramref name="samples"/> picking roughly <paramref name="maxPoints"/> of
    /// them, always preserving the first and last sample and both endpoints of every gap wider than
    /// <paramref name="gapThreshold"/>, so a downtime marker or the overall range can never be
    /// smoothed away. Gap detection always runs against the original timestamps.
    /// </summary>
    public static Result Reduce(IReadOnlyList<BatterySample> samples, int maxPoints, TimeSpan gapThreshold)
    {
        var trueGapAfter = new HashSet<int>();   // original-index space: index i = gap between i-1 and i
        for (int i = 1; i < samples.Count; i++)
            if (samples[i].AtUtc - samples[i - 1].AtUtc > gapThreshold)
                trueGapAfter.Add(i);

        if (maxPoints <= 0 || samples.Count <= maxPoints)
            return new(samples, trueGapAfter);   // indices are already in the right space

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

        // Both endpoints of every gap are in mustKeep, so both are present in indexMap.
        var reducedGapIndices = new HashSet<int>();
        foreach (int origGapIdx in trueGapAfter)
            reducedGapIndices.Add(indexMap[origGapIdx]);

        return new(reduced, reducedGapIndices);
    }
}
