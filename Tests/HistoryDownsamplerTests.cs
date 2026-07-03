using LenovoTray.Helpers;
using LenovoTray.Services;
using Xunit;

namespace LenovoTray.Tests;

public class HistoryDownsamplerTests
{
    private static BatterySample Sample(int minutesFromEpoch, int soc) =>
        new(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(minutesFromEpoch), soc, null, 0);

    [Fact]
    public void Reduce_ReturnsInputUnchanged_WhenAlreadyAtOrBelowTarget()
    {
        var samples = Enumerable.Range(0, 50).Select(i => Sample(i, i)).ToList();

        var result = HistoryDownsampler.Reduce(samples, maxPoints: 100, gapThreshold: TimeSpan.FromMinutes(5));

        Assert.Same(samples, result.Samples);   // no reduction needed → same reference, not a copy
    }

    [Fact]
    public void Reduce_ReducesPointCount_ButKeepsFirstAndLast()
    {
        var samples = Enumerable.Range(0, 10_000).Select(i => Sample(i, i % 100)).ToList();

        var result = HistoryDownsampler.Reduce(samples, maxPoints: 500, gapThreshold: TimeSpan.FromMinutes(5));

        Assert.True(result.Samples.Count < samples.Count);
        Assert.True(result.Samples.Count <= 510);   // small slack for must-keep boundary points
        Assert.Equal(samples[0],  result.Samples[0]);
        Assert.Equal(samples[^1], result.Samples[^1]);
    }

    [Fact]
    public void Reduce_NeverDropsAGapBoundary()
    {
        // A long dense run, then a single sample 2 hours later (a real "restart" gap), then one
        // more sample — the gap's boundary points must survive regardless of where the stride
        // would otherwise land.
        var before  = Enumerable.Range(0, 5000).Select(i => Sample(i, 50)).ToList();
        var after   = Sample(5000 + 120, 51);
        var samples = before.Append(after).ToList();

        var result = HistoryDownsampler.Reduce(samples, maxPoints: 100, gapThreshold: TimeSpan.FromMinutes(60));

        Assert.Contains(before[^1], result.Samples);   // sample immediately before the gap
        Assert.Contains(after,      result.Samples);   // sample immediately after the gap
    }

    [Fact]
    public void Reduce_MarksTrueGapBoundary_ByIndexInReducedList()
    {
        var before  = Enumerable.Range(0, 5000).Select(i => Sample(i, 50)).ToList();
        var after   = Sample(5000 + 120, 51);   // 120 minutes later — a real gap
        var samples = before.Append(after).ToList();

        var result = HistoryDownsampler.Reduce(samples, maxPoints: 100, gapThreshold: TimeSpan.FromMinutes(60));

        // The gap boundary must be reported using an index into the REDUCED list (result.Samples),
        // not the original — that index should point at the "after" sample.
        int afterIndex = result.Samples.ToList().IndexOf(after);
        Assert.True(afterIndex > 0);
        Assert.Contains(afterIndex, result.GapBeforeIndices);
    }

    [Fact]
    public void Reduce_DoesNotTreatOrdinaryStrideSpacingAsAGap()
    {
        // 10,000 samples 1 minute apart, no real downtime anywhere. Downsampled to 100 points,
        // adjacent reduced points end up ~100 minutes apart — far more than a typical gap
        // threshold — but since nothing in the ORIGINAL data exceeded gapThreshold, none of that
        // stride spacing should be reported as a gap.
        var samples = Enumerable.Range(0, 10_000).Select(i => Sample(i, i % 100)).ToList();

        var result = HistoryDownsampler.Reduce(samples, maxPoints: 100, gapThreshold: TimeSpan.FromMinutes(60));

        Assert.Empty(result.GapBeforeIndices);
    }

    [Fact]
    public void Reduce_OutputStaysChronologicallyOrdered()
    {
        var samples = Enumerable.Range(0, 3000).Select(i => Sample(i, i % 100)).ToList();

        var result = HistoryDownsampler.Reduce(samples, maxPoints: 200, gapThreshold: TimeSpan.FromMinutes(5));

        for (int i = 1; i < result.Samples.Count; i++)
            Assert.True(result.Samples[i].AtUtc >= result.Samples[i - 1].AtUtc);
    }

    [Fact]
    public void Reduce_ZeroOrNegativeMaxPoints_ReturnsInputUnchanged()
    {
        var samples = Enumerable.Range(0, 10).Select(i => Sample(i, i)).ToList();

        var result = HistoryDownsampler.Reduce(samples, maxPoints: 0, gapThreshold: TimeSpan.FromMinutes(1));

        Assert.Same(samples, result.Samples);
    }
}
