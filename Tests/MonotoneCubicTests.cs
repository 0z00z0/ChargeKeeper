using ChargeKeeper.Helpers;
using Xunit;

namespace ChargeKeeper.Tests;

// The charge line is a curve, and a curve on a diagnostic graph is only honest while it obeys two
// promises: it passes through every knot it was given, and between two knots it never leaves the
// band those two knots span. The second is the one that matters — a fit that bulges past its own
// endpoints draws a battery level that was never recorded. Both are asserted by sweeping the curve
// densely rather than by trusting the tangent formula, so replacing the interpolation with one that
// overshoots turns these red.
public class MonotoneCubicTests
{
    // Fine enough that a bulge of a fraction of a percent between two knots cannot slip through.
    private const int SweepSteps = 200;

    private static double[] Sweep(IReadOnlyList<double> xs, IReadOnlyList<double> ys, out double[] outXs)
    {
        var m      = MonotoneCubic.Tangents(xs, ys);
        var points = new List<double>();
        var atX    = new List<double>();
        for (int k = 0; k < xs.Count - 1; k++)
            for (int s = 0; s <= SweepSteps; s++)
            {
                double x = xs[k] + (xs[k + 1] - xs[k]) * s / (double)SweepSteps;
                atX.Add(x);
                points.Add(MonotoneCubic.Evaluate(xs, ys, m, x));
            }
        outXs = [.. atX];
        return [.. points];
    }

    /// <summary>A staircase of whole percentage points, exactly the shape the battery reports.</summary>
    private static (double[] Xs, double[] Ys) Staircase()
    {
        var xs = new List<double>();
        var ys = new List<double>();
        int level = 50;
        for (int block = 0; block < 8; block++)
        {
            for (int i = 0; i < 6; i++) { xs.Add(xs.Count); ys.Add(level); }
            level++;
        }
        return ([.. xs], [.. ys]);
    }

    [Fact]
    public void PassesThroughEverySample()
    {
        var (xs, ys) = Staircase();
        var m = MonotoneCubic.Tangents(xs, ys);

        for (int i = 0; i < xs.Length; i++)
            Assert.Equal(ys[i], MonotoneCubic.Evaluate(xs, ys, m, xs[i]), precision: 9);
    }

    [Fact]
    public void PassesThroughEverySample_OnAnIrregularSeries()
    {
        double[] xs = [0, 3, 4, 9, 15, 16, 30];
        double[] ys = [80, 79, 79, 62, 61, 61, 12];
        var m = MonotoneCubic.Tangents(xs, ys);

        for (int i = 0; i < xs.Length; i++)
            Assert.Equal(ys[i], MonotoneCubic.Evaluate(xs, ys, m, xs[i]), precision: 9);
    }

    [Fact]
    public void NeverLeavesTheBandBetweenNeighbours()
    {
        var (xs, ys) = Staircase();
        var m = MonotoneCubic.Tangents(xs, ys);

        for (int k = 0; k < xs.Length - 1; k++)
        {
            double lo = Math.Min(ys[k], ys[k + 1]);
            double hi = Math.Max(ys[k], ys[k + 1]);
            for (int s = 0; s <= SweepSteps; s++)
            {
                double x = xs[k] + (xs[k + 1] - xs[k]) * s / (double)SweepSteps;
                double y = MonotoneCubic.Evaluate(xs, ys, m, x);
                Assert.InRange(y, lo - 1e-9, hi + 1e-9);
            }
        }
    }

    // A single spike is where a plain cubic spline bulges worst: it undershoots on the way up and
    // overshoots on the way down, drawing levels either side of the spike that were never recorded.
    [Fact]
    public void NeverLeavesTheBandBetweenNeighbours_AcrossASpike()
    {
        double[] xs = [0, 1, 2, 3, 4, 5, 6];
        double[] ys = [40, 40, 40, 95, 40, 40, 40];
        var m = MonotoneCubic.Tangents(xs, ys);

        for (int k = 0; k < xs.Length - 1; k++)
        {
            double lo = Math.Min(ys[k], ys[k + 1]);
            double hi = Math.Max(ys[k], ys[k + 1]);
            for (int s = 0; s <= SweepSteps; s++)
            {
                double x = xs[k] + (xs[k + 1] - xs[k]) * s / (double)SweepSteps;
                double y = MonotoneCubic.Evaluate(xs, ys, m, x);
                Assert.InRange(y, lo - 1e-9, hi + 1e-9);
            }
        }
    }

    [Fact]
    public void StaysWithinTheWholeSeriesRange()
    {
        var (xs, ys) = Staircase();
        var swept = Sweep(xs, ys, out _);

        Assert.InRange(swept.Min(), ys.Min() - 1e-9, double.MaxValue);
        Assert.InRange(swept.Max(), double.MinValue, ys.Max() + 1e-9);
    }

    [Fact]
    public void PreservesDirection_RisingSeriesNeverFalls()
    {
        var (xs, ys) = Staircase();
        var swept = Sweep(xs, ys, out _);

        for (int i = 1; i < swept.Length; i++)
            Assert.True(swept[i] >= swept[i - 1] - 1e-9,
                $"The fit fell at step {i} on a series that only rises.");
    }

    [Fact]
    public void PreservesDirection_FallingSeriesNeverRises()
    {
        double[] xs = [0, 1, 2, 3, 4, 5, 6, 7];
        double[] ys = [88, 88, 87, 87, 86, 84, 84, 81];
        var swept = Sweep(xs, ys, out _);

        for (int i = 1; i < swept.Length; i++)
            Assert.True(swept[i] <= swept[i - 1] + 1e-9,
                $"The fit rose at step {i} on a series that only falls.");
    }

    // Between two equal readings there is no evidence of movement, so the curve must not invent any.
    [Fact]
    public void HoldsFlatAcrossAPlateau()
    {
        double[] xs = [0, 1, 2, 3, 4, 5];
        double[] ys = [60, 70, 70, 70, 70, 55];
        var m = MonotoneCubic.Tangents(xs, ys);

        for (int s = 0; s <= SweepSteps; s++)
        {
            double x = 1 + 3.0 * s / SweepSteps;
            Assert.Equal(70, MonotoneCubic.Evaluate(xs, ys, m, x), precision: 9);
        }
    }

    [Fact]
    public void ClampsOutsideTheKnotRange()
    {
        double[] xs = [10, 20, 30];
        double[] ys = [40, 50, 45];
        var m = MonotoneCubic.Tangents(xs, ys);

        Assert.Equal(40, MonotoneCubic.Evaluate(xs, ys, m, -100), precision: 9);
        Assert.Equal(45, MonotoneCubic.Evaluate(xs, ys, m, 999),  precision: 9);
    }

    [Fact]
    public void SingleKnot_EvaluatesToThatKnot()
    {
        double[] xs = [7];
        double[] ys = [63];
        Assert.Equal(63, MonotoneCubic.Evaluate(xs, ys, MonotoneCubic.Tangents(xs, ys), 7), precision: 9);
    }

    [Fact]
    public void NoKnots_Throws() =>
        Assert.Throws<ArgumentException>(() => MonotoneCubic.Evaluate([], [], [], 0));
}
