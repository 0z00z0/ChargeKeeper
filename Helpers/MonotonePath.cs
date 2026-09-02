using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace ChargeKeeper.Helpers;

/// <summary>
/// Builds the drawable figure for a monotone-cubic series: knots plus precomputed tangents in, one
/// <see cref="PathFigure"/> out. Shared by every graph in the app, so two plots cannot drift into
/// two different curves through the same points.
/// </summary>
/// <remarks>Renderer-side companion to <see cref="MonotoneCubic"/>, which produces the tangents and
/// knows nothing about XAML. Nothing here is series-aware: it takes points, not samples.</remarks>
internal static class MonotonePath
{
    /// <summary>Interpolating figure through <paramref name="pts"/> from precomputed tangents
    /// <paramref name="m"/>. <paramref name="closeAtY"/> closes it for a gradient fill; left open,
    /// it is the line itself.</summary>
    internal static PathFigure Figure(IReadOnlyList<Point> pts, double[] m, double? closeAtY = null)
    {
        ArgumentNullException.ThrowIfNull(pts);
        ArgumentNullException.ThrowIfNull(m);

        var figure = new PathFigure { StartPoint = pts[0], IsClosed = closeAtY.HasValue };
        for (int i = 0; i < pts.Count - 1; i++)
        {
            double h = pts[i + 1].X - pts[i].X;
            figure.Segments.Add(new BezierSegment
            {
                // Hermite to Bezier for a curve linear in x: the tangent vector at each end is
                // (h, slope*h), and the control points sit a third of it in from each endpoint.
                Point1 = new Point(pts[i].X     + h / 3, pts[i].Y     + m[i]     * h / 3),
                Point2 = new Point(pts[i + 1].X - h / 3, pts[i + 1].Y - m[i + 1] * h / 3),
                Point3 = pts[i + 1],
            });
        }

        if (closeAtY is { } bottomY)
        {
            figure.Segments.Add(new LineSegment { Point = new Point(pts[^1].X, bottomY) });
            figure.Segments.Add(new LineSegment { Point = new Point(pts[0].X,  bottomY) });
        }

        return figure;
    }
}
