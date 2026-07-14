using ChargeKeeper.Helpers;
using Xunit;

namespace ChargeKeeper.Tests;

// MonotoneSearch.NearestIndex backs the hover crosshair's "which sample is under the cursor" lookup.
// It replaced an O(n) linear scan with a binary search over the graph's monotone compressed-X
// coordinates, so these pin the equivalence (same answer as the old first-wins scan) plus the edge
// cases a binary search can get wrong: empty/single lists, targets outside the range, and ties.
public class MonotoneSearchTests
{
    // Reference implementation matching the old linear scan (first-wins on a tie).
    private static int LinearNearest(IReadOnlyList<double> xs, double x)
    {
        int nearest = 0;
        double best = double.MaxValue;
        for (int i = 0; i < xs.Count; i++)
        {
            double d = System.Math.Abs(xs[i] - x);
            if (d < best) { best = d; nearest = i; }
        }
        return nearest;
    }

    [Fact]
    public void Empty_ReturnsMinusOne() =>
        Assert.Equal(-1, MonotoneSearch.NearestIndex([], 5));

    [Fact]
    public void Single_ReturnsZero() =>
        Assert.Equal(0, MonotoneSearch.NearestIndex([42.0], -1000));

    [Theory]
    [InlineData(-5, 0)]     // before the first element → clamps to index 0
    [InlineData(0, 0)]
    [InlineData(2.4, 1)]    // closer to 2 (index 1) than 4
    [InlineData(3.0, 1)]    // exactly between 2 (idx1) and 4 (idx2) → tie resolves to the lower index
    [InlineData(3.1, 2)]    // just past the midpoint → the 4
    [InlineData(100, 4)]    // past the last element → clamps to the last index
    public void MatchesExpected_OnSimpleAscendingList(double target, int expected)
    {
        double[] xs = [0, 2, 4, 6, 8];
        Assert.Equal(expected, MonotoneSearch.NearestIndex(xs, target));
    }

    [Fact]
    public void HandlesDuplicateValues_ReturnsAnEquallyNearIndex()
    {
        // Compressed-X can hold equal consecutive coordinates (a zero-width step or collapsed gap).
        // Which duplicate is returned is unspecified, but its value must be exactly as near to the
        // target as the linear scan's pick — i.e. the answer is a legitimate nearest.
        double[] xs = [0, 5, 5, 5, 9];
        for (double t = -1; t <= 10; t += 0.5)
        {
            int got = MonotoneSearch.NearestIndex(xs, t);
            double bestDist = System.Math.Abs(xs[LinearNearest(xs, t)] - t);
            Assert.Equal(bestDist, System.Math.Abs(xs[got] - t), precision: 9);
        }
    }

    [Fact]
    public void AgreesWithLinearScan_AcrossAMonotoneList()
    {
        var xs = new double[200];
        double v = 0;
        var rng = new System.Random(1234);
        for (int i = 0; i < xs.Length; i++) { v += rng.NextDouble() * 3; xs[i] = v; }

        for (double t = -5; t <= v + 5; t += 0.37)
            Assert.Equal(LinearNearest(xs, t), MonotoneSearch.NearestIndex(xs, t));
    }
}
