using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using ChargeKeeper.Helpers;
using ChargeKeeper.Services;

namespace ChargeKeeper.UI;

/// <summary>Battery health / degradation trend: capacity lost since new (or since tracking began)
/// plus an actual-and-projected line of full-charge capacity. Deliberately simpler than the SoC
/// graph — capacity data is at most one point per day, so there is nothing to smooth, downsample or
/// gap-detect.</summary>
public sealed partial class BatteryHealthPanel : UserControl
{
    // Project half the real span forward, capped so a long history can't draw a low-confidence line.
    private const double ProjectionFraction = 0.5;
    private const int    MaxProjectionDays  = 180;

    // Loaded once per panel lifetime: SizeChanged → Render fires on every layout pass, and nothing
    // in the capacity file can change within a session.
    private IReadOnlyList<CapacitySample>? _samples;

    public BatteryHealthPanel()
    {
        InitializeComponent();
    }

    // The canvas has zero size until the first real layout pass, so this drives the first render.
    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        try { Render(); }
        catch (Exception ex) { AppLog.Error("BatteryHealthPanel.Render", ex); }
    }

    /// <summary>Redraws from the session-cached capacity history. There is no refresh timer, since
    /// nothing here can change within a single pop-out session.</summary>
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

        // "Since new" only when the controller reports a usable design capacity. A non-positive one
        // is treated as absent — the persisted CSV round-trips a 0 back as 0, not null, and dividing
        // by it would report a confident 0% loss and then plot NaN.
        int?   design      = samples[0].DesignMwh is > 0 ? samples[0].DesignMwh : null;
        double baselineMwh = design ?? samples[0].FullChargeMwh;
        double latestMwh   = samples[^1].FullChargeMwh;
        double lostPercent = Math.Max(0, (baselineMwh - latestMwh) / baselineMwh * 100);
        string basis       = design is not null ? "since new" : "since tracking began";
        SummaryText.Text   = $"{lostPercent:0.#}% capacity lost {basis} ({samples.Count} days tracked)";

        double w = TrendCanvas.ActualWidth;
        double h = TrendCanvas.ActualHeight;
        if (w < 4 || h < 4) return;

        // As a % of baseline, so the Y-axis reads directly as "capacity remaining".
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
