namespace ChargeKeeper.Helpers;

/// <summary>Ordinary least-squares fit over (x, y) points.</summary>
internal static class LinearRegression
{
    /// <summary>Fits y = slope*x + intercept. Degenerate input — every point sharing one X — returns
    /// a flat line at the first point's Y rather than dividing by zero.</summary>
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
