using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace ChargeKeeper.Helpers;

/// <summary>
/// The one arc the application draws rings from: the dashboard's battery gauge and the focus
/// session cover's countdown are the same shape at different sizes, so neither builds its own.
/// </summary>
/// <remarks>Angles follow the clock face — 0° is 12 o'clock and a positive sweep runs
/// clockwise.</remarks>
internal static class RingGeometry
{
    /// <summary>Where a gauge arc starts and how far round it can run. Shared so a ring drawn
    /// anywhere else reads as the same instrument.</summary>
    internal const double StartAngle = 135;

    internal const double Sweep = 270;

    /// <summary>Circular-arc geometry, or null for a sweep of nothing — a zero-length arc still
    /// paints its round line caps as a dot.</summary>
    internal static Geometry? Arc(double cx, double cy, double r, double startDeg, double sweepDeg)
    {
        if (sweepDeg <= 0) return null;

        // A full 360° arc is degenerate in SVG/XAML — cap slightly below.
        sweepDeg = Math.Min(sweepDeg, 359.99);

        // Rotate reference frame: clock-face 0° maps to math 270° (i.e. subtract 90°).
        double startRad = (startDeg - 90) * Math.PI / 180;
        double endRad   = (startDeg + sweepDeg - 90) * Math.PI / 180;

        var startPt = new Point(cx + r * Math.Cos(startRad), cy + r * Math.Sin(startRad));
        var endPt   = new Point(cx + r * Math.Cos(endRad),   cy + r * Math.Sin(endRad));

        var figure = new PathFigure { StartPoint = startPt, IsClosed = false };
        figure.Segments.Add(new ArcSegment
        {
            Point          = endPt,
            Size           = new Size(r, r),
            IsLargeArc     = sweepDeg > 180,
            SweepDirection = SweepDirection.Clockwise,
            RotationAngle  = 0,
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }
}
