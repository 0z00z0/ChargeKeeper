using ChargeKeeper.Helpers;
using Xunit;

namespace ChargeKeeper.Tests;

public class LinearRegressionTests
{
    [Fact]
    public void Fit_PerfectLine_RecoversExactSlopeAndIntercept()
    {
        // y = 2x + 1
        var points = new (double X, double Y)[] { (0, 1), (1, 3), (2, 5), (3, 7) };

        var (slope, intercept) = LinearRegression.Fit(points);

        Assert.Equal(2, slope, precision: 9);
        Assert.Equal(1, intercept, precision: 9);
    }

    [Fact]
    public void Fit_FlatData_ZeroSlope()
    {
        var points = new (double X, double Y)[] { (0, 50), (1, 50), (2, 50) };

        var (slope, intercept) = LinearRegression.Fit(points);

        Assert.Equal(0, slope, precision: 9);
        Assert.Equal(50, intercept, precision: 9);
    }

    [Fact]
    public void Fit_AllSameX_DoesNotThrow_ReturnsFlatLineAtFirstY()
    {
        // Degenerate input (denom == 0) must fall back gracefully, not divide by zero / return NaN.
        var points = new (double X, double Y)[] { (5, 10), (5, 20), (5, 30) };

        var (slope, intercept) = LinearRegression.Fit(points);

        Assert.Equal(0, slope);
        Assert.Equal(10, intercept);
        Assert.False(double.IsNaN(slope));
        Assert.False(double.IsNaN(intercept));
    }

    [Fact]
    public void Fit_NoisyData_TrendDirectionMatchesObviousSlope()
    {
        // Not a precise value assertion (real degradation data is noisy) — just confirms the fit
        // finds the obviously-declining trend and not something nonsensical.
        var points = new (double X, double Y)[] { (0, 100), (1, 99), (2, 99.5), (3, 98), (4, 97.5) };

        var (slope, _) = LinearRegression.Fit(points);

        Assert.True(slope < 0);
    }
}
