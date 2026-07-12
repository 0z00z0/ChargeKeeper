using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using ChargeKeeper.Helpers;
using ChargeKeeper.Services;

namespace ChargeKeeper.UI;

/// <summary>
/// Battery health / degradation trend panel (TODO #24) — a separate section below the main history
/// graph showing "capacity lost since new/tracking began" and a simple actual-plus-projected line
/// of <c>FullChargeCapacity</c> over time. Deliberately much simpler than
/// <see cref="BatteryHistoryGraphControl"/> (straight line segments, no smoothing, no interaction,
/// no gap handling): this is a secondary, slow-timescale indicator, not a second full-featured
/// graph, and capacity data is at most one point per day — there's nothing to smooth or downsample,
/// and a missed day or two isn't "downtime" the way an hour without a SoC sample is.
/// </summary>
public sealed partial class BatteryHealthPanel : UserControl
{
    // How far past the last real sample the projected trend line extends, as a fraction of the
    // real data's own span (90 days of history projects 45 days further out), capped separately so
    // a very long history doesn't project a wildly long, low-confidence line.
    private const double ProjectionFraction = 0.5;
    private const int    MaxProjectionDays  = 180;

    // Loaded once per panel lifetime (= one pop-out session). SizeChanged → Render fires on every
    // layout pass — ~every 10ms for the host window's 340ms open/retract animations, plus every
    // user resize — and nothing in the capacity file can change within a session (at most one row
    // per day), so re-reading the CSV from disk on each of those passes was pure waste.
    private IReadOnlyList<CapacitySample>? _samples;

    public BatteryHealthPanel()
    {
        InitializeComponent();
    }

    // The canvas has zero size until the first real layout pass, so — same idiom as
    // BatteryHistoryGraphControl's own OnCanvasSizeChanged — this is what actually triggers the
    // first successful render; no separate Loaded handler needed.
    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e) => Render();

    /// <summary>
    /// Redraws from the session-cached capacity history (read from disk once, on the first render —
    /// see <see cref="_samples"/>). Unlike the main graph, this panel has no periodic refresh
    /// timer, since nothing here can change within a single pop-out session.
    /// </summary>
    public void Render()
    {
        var samples = _samples ??= BatteryCapacityHistoryService.LoadAll();
        TrendCanvas.Children.Clear();

        if (samples.Count < 2)
        {
            SummaryText.Text = samples.Count == 0
                ? "Gathering data — check back in a few days."
                : "Gathering data — one more day of tracking will show a trend.";
            return;
        }

        // "Since new" when the controller reports design capacity (closest to the TODO's literal
        // "since install"); honestly falls back to "since tracking began" otherwise rather than
        // implying a precision the data doesn't have.
        double baselineMwh = samples[0].DesignMwh ?? samples[0].FullChargeMwh;
        double latestMwh   = samples[^1].FullChargeMwh;
        double lostPercent = Math.Max(0, (baselineMwh - latestMwh) / baselineMwh * 100);
        string basis       = samples[0].DesignMwh is not null ? "since new" : "since tracking began";
        SummaryText.Text   = $"{lostPercent:0.#}% capacity lost {basis} ({samples.Count} days tracked)";

        double w = TrendCanvas.ActualWidth;
        double h = TrendCanvas.ActualHeight;
        if (w < 4 || h < 4) return;

        // Plot capacity as a % of baseline so the Y-axis reads directly as "capacity remaining",
        // matching the summary line above it.
        var points = new List<(double Days, double Pct)>(samples.Count);
        foreach (var s in samples)
            points.Add(((s.AtUtc - samples[0].AtUtc).TotalDays, s.FullChargeMwh / baselineMwh * 100));

        double minPct = Math.Min(80, points.Min(p => p.Pct)) - 2;   // headroom below the lowest point
        double maxPct = Math.Max(100, points.Max(p => p.Pct)) + 1;  // headroom above 100% (design can read slightly high)
        double range  = Math.Max(maxPct - minPct, 1);

        double lastDay          = points[^1].Days;
        var (slope, intercept)  = LinearRegression.Fit(points);
        double projectionDays   = Math.Min(Math.Max(lastDay, 1) * ProjectionFraction, MaxProjectionDays);
        double totalDays        = Math.Max(lastDay + projectionDays, 1);

        double ProjectX(double day) => day / totalDays * w;
        double ProjectY(double pct) => h - (pct - minPct) / range * h;

        var actual = new Microsoft.UI.Xaml.Shapes.Polyline
        {
            Stroke             = AppColors.HistorySocBrush,
            StrokeThickness    = 2,
            StrokeLineJoin     = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap   = PenLineCap.Round,
        };
        foreach (var (day, pct) in points)
            actual.Points.Add(new Point(ProjectX(day), ProjectY(pct)));
        TrendCanvas.Children.Add(actual);

        if (projectionDays > 0)
        {
            double projectedEndDay = lastDay + projectionDays;
            var projected = new Microsoft.UI.Xaml.Shapes.Line
            {
                X1 = ProjectX(lastDay), Y1 = ProjectY(points[^1].Pct),
                X2 = ProjectX(projectedEndDay), Y2 = ProjectY(slope * projectedEndDay + intercept),
                Stroke          = AppColors.HistorySocBrush,
                StrokeThickness = 1.5,
                StrokeDashArray = [3, 2],
                Opacity         = 0.6,
            };
            TrendCanvas.Children.Add(projected);
        }
    }
}
