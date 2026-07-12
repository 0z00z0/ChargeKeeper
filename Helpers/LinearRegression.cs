namespace ChargeKeeper.Helpers;

/// <summary>
/// Ordinary least-squares fit over (x, y) points — used by <see cref="ChargeKeeper.UI.BatteryHealthPanel"/>
/// for its capacity-degradation trend projection (TODO #24). Deliberately a plain, non-WinUI-typed
/// helper (unlike the UserControl that calls it) so it's directly unit-testable: the Tests project
/// targets a Windows TFM but does NOT set UseWinUI, so a type that (even transitively) requires the
/// WinAppSDK runtime — like a UserControl subclass — isn't safely callable from there.
/// </summary>
internal static class LinearRegression
{
    /// <summary>
    /// Fits y = slope*x + intercept. A rough visual trend, not a scientific projection — the
    /// caller's data is sparse and noisy enough that anything fancier wouldn't be more honest, just
    /// more complicated. Degenerate input (all points share one X, so there's no meaningful slope)
    /// returns a flat line at the first point's Y rather than dividing by zero.
    /// </summary>
    public static (double Slope, double Intercept) Fit(IReadOnlyList<(double X, double Y)> points)
    {
        int n = points.Count;
        double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
        foreach (var (x, y) in points) { sumX += x; sumY += y; sumXY += x * y; sumXX += x * x; }
        double denom = n * sumXX - sumX * sumX;
        if (Math.Abs(denom) < 1e-9) return (0, points[0].Y);
        double slope     = (n * sumXY - sumX * sumY) / denom;
        double intercept = (sumY - slope * sumX) / n;
        return (slope, intercept);
    }
}
