namespace ChargeKeeper.Helpers;

/// <summary>
/// Monotone cubic Hermite interpolation (Fritsch-Carlson) over a strictly increasing x sequence.
/// Renderer-free: plain doubles, so the guarantees can be asserted without a canvas.
///
/// The curve interpolates every knot exactly, and on each interval stays within the two knots' own
/// y values — it never rises above nor falls below its own endpoints. That is what makes it usable
/// on a diagnostic graph, where a curve passing through a value the data never carried would be a
/// different kind of wrong.
/// </summary>
internal static class MonotoneCubic
{
    /// <summary>Tangents for (xs[i], ys[i]). The final pass caps every tangent so no segment can
    /// rise or fall past its own two endpoints.</summary>
    internal static double[] Tangents(IReadOnlyList<double> xs, IReadOnlyList<double> ys)
    {
        ArgumentNullException.ThrowIfNull(xs);
        ArgumentNullException.ThrowIfNull(ys);

        int n = xs.Count;
        var m = new double[n];
        if (n < 2) return m;

        var d = new double[n - 1];
        for (int k = 0; k < n - 1; k++)
        {
            double dx = xs[k + 1] - xs[k];
            d[k] = dx > 0 ? (ys[k + 1] - ys[k]) / dx : 0;
        }

        static int Sign(double v) => v > 0 ? 1 : v < 0 ? -1 : 0;

        m[0]     = d[0];
        m[n - 1] = d[n - 2];
        for (int k = 1; k < n - 1; k++)
        {
            bool sameSign = d[k - 1] != 0 && Sign(d[k - 1]) == Sign(d[k]);
            m[k] = sameSign ? (d[k - 1] + d[k]) / 2 : 0;
        }

        for (int k = 0; k < n - 1; k++)
        {
            if (d[k] == 0) { m[k] = 0; m[k + 1] = 0; continue; }
            double alpha = m[k] / d[k];
            double beta  = m[k + 1] / d[k];
            if (alpha < 0) m[k] = 0;
            if (beta  < 0) m[k + 1] = 0;
            double s = alpha * alpha + beta * beta;
            if (s > 9)
            {
                double tau = 3 / Math.Sqrt(s);
                m[k]     = tau * alpha * d[k];
                m[k + 1] = tau * beta  * d[k];
            }
        }
        return m;
    }

    /// <summary>The curve's y at <paramref name="x"/>, from tangents produced by
    /// <see cref="Tangents"/>. Outside the knot range the nearest end value is returned rather than
    /// extrapolated: past the data there is nothing to interpolate between.</summary>
    internal static double Evaluate(
        IReadOnlyList<double> xs, IReadOnlyList<double> ys, IReadOnlyList<double> m, double x)
    {
        ArgumentNullException.ThrowIfNull(xs);
        ArgumentNullException.ThrowIfNull(ys);
        ArgumentNullException.ThrowIfNull(m);
        if (xs.Count == 0) throw new ArgumentException("At least one knot is required.", nameof(xs));

        if (xs.Count == 1 || x <= xs[0]) return ys[0];
        if (x >= xs[^1]) return ys[^1];

        int k = MonotoneSearch.NearestIndex(xs, x);
        // NearestIndex snaps to the closest knot; step back when that knot sits to the right of x.
        if (xs[k] > x) k--;
        if (k >= xs.Count - 1) k = xs.Count - 2;
        if (k < 0) k = 0;

        double h = xs[k + 1] - xs[k];
        if (h <= 0) return ys[k];

        double t  = (x - xs[k]) / h;
        double t2 = t * t;
        double t3 = t2 * t;

        // Hermite basis: values at both ends, tangents scaled by the interval width.
        return (2 * t3 - 3 * t2 + 1) * ys[k]
             + (t3 - 2 * t2 + t)     * h * m[k]
             + (-2 * t3 + 3 * t2)    * ys[k + 1]
             + (t3 - t2)             * h * m[k + 1];
    }
}
