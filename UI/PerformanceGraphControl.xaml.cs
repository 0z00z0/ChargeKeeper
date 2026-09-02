using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using ChargeKeeper.Helpers;
using ChargeKeeper.Services;

namespace ChargeKeeper.UI;

/// <summary>
/// The self-measurement plot: processor share of the whole machine on the left axis, working set on
/// the right, over one fixed window.
/// </summary>
/// <remarks>
/// <para>The two lines are sampled at different rates on purpose, so the legend names each rate
/// beside its own swatch. At the slow end of the range the once-a-second memory line is the denser
/// of the two, which reads as a defect unless the interface says which is which.</para>
/// <para>The curve comes from <see cref="MonotoneCubic"/> and <see cref="MonotonePath"/>, the same
/// interpolation and figure builder the battery history graph draws with, and the colours from
/// <see cref="AppColors"/>. Nothing here decides a colour or fits a curve of its own.</para>
/// <para>The repaint timer runs only while the control is loaded AND the feature is on. Switched
/// off, this schedules nothing either.</para>
/// </remarks>
public sealed partial class PerformanceGraphControl : UserControl
{
    private const double ProcessorStrokeWidth = 2.5;
    private const double MemoryStrokeWidth    = 2.0;

    // Below this the processor axis stops shrinking: an idle tray app would otherwise have its own
    // rounding noise drawn as a full-height mountain range.
    private const double MinProcessorAxisTop = 5.0;

    // Four repaints a second. Fast enough to look live at 10 Hz, and unrelated to the sample rate:
    // this draws what has already been collected and never causes a sample.
    private static readonly TimeSpan RepaintInterval = TimeSpan.FromMilliseconds(250);

    private readonly DispatcherTimer _repaintTimer;
    private bool _loaded;

    /// <summary>Plot canvas row height: fixed by the Settings page, "*" in a resizable host.</summary>
    public GridLength PlotAreaHeight
    {
        get => CanvasRow.Height;
        set => CanvasRow.Height = value;
    }

    public PerformanceGraphControl()
    {
        InitializeComponent();

        LegendProcessorSwatch.Background = AppColors.PerformanceProcessorBrush;
        LegendMemorySwatch.Background    = AppColors.PerformanceMemoryBrush;

        _repaintTimer = new DispatcherTimer { Interval = RepaintInterval };
        _repaintTimer.Tick += (_, _) => Render();

        Loaded   += (_, _) => { _loaded = true;  ApplySettings(); };
        Unloaded += (_, _) => { _loaded = false; _repaintTimer.Stop(); };
    }

    /// <summary>
    /// Re-reads the settings and starts or stops the repaint accordingly. Called on load and
    /// whenever the switch or the rate changes, so no restart is needed.
    /// </summary>
    public void ApplySettings()
    {
        var settings = SettingsService.Current;
        bool on = settings.PerformanceGraphEnabled;

        LegendProcessorText.Text = $"Processor · {settings.PerformanceSampleRate.Label()}";
        LegendMemoryText.Text    = "Memory · 1 Hz";

        if (on && _loaded)
        {
            if (!_repaintTimer.IsEnabled) _repaintTimer.Start();
        }
        else
        {
            _repaintTimer.Stop();
        }

        Render();
    }

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e) => Render();

    /// <summary>Redraws both series. Never throws: a drawing fault must not take the window down.</summary>
    public void Render()
    {
        try
        {
            RenderCore();
        }
        catch (Exception ex)
        {
            AppLog.Error("PerformanceGraphControl.Render", ex);
        }
    }

    private void RenderCore()
    {
        PlotCanvas.Children.Clear();

        double w = PlotCanvas.ActualWidth;
        double h = PlotCanvas.ActualHeight;
        if (w <= 1 || h <= 1) return;

        var processor = PerformanceHistoryService.ProcessorWindow();
        var memory    = PerformanceHistoryService.ResourceWindow();

        var span     = PerformanceHistoryService.WindowSpan;
        var endUtc   = DateTime.UtcNow;
        var startUtc = endUtc - span;

        AxisStartLabel.Text = FormatSpan(span);
        UpdateStateLabel(processor.Count, memory.Count);
        UpdateCounts(memory);

        // Left axis: processor share, always anchored at zero so the height of the line is the
        // reading rather than the difference between two readings.
        double processorTop = Math.Max(MinProcessorAxisTop, processor.Count == 0 ? 0 : processor.Max(r => r.Percent));
        processorTop = NiceCeiling(processorTop);
        LeftAxisTopLabel.Text    = Percent(processorTop);
        LeftAxisMidLabel.Text    = Percent(processorTop / 2);
        LeftAxisBottomLabel.Text = Percent(0);

        // Right axis: working set, scaled to what the window actually holds — this figure moves by a
        // few megabytes, so a zero-anchored axis would show a flat line near the top.
        double memMinMb = 0, memMaxMb = 0;
        if (memory.Count > 0)
        {
            memMinMb = memory.Min(r => r.WorkingSetKb) / 1024.0;
            memMaxMb = memory.Max(r => r.WorkingSetKb) / 1024.0;
            if (memMaxMb - memMinMb < 1) { memMinMb = Math.Max(0, memMinMb - 0.5); memMaxMb = memMinMb + 1; }
        }
        RightAxisTopLabel.Text    = memory.Count == 0 ? "—" : Megabytes(memMaxMb);
        RightAxisMidLabel.Text    = memory.Count == 0 ? "—" : Megabytes((memMinMb + memMaxMb) / 2);
        RightAxisBottomLabel.Text = memory.Count == 0 ? "—" : Megabytes(memMinMb);

        // Memory first, so the faster processor line is drawn over it rather than under.
        DrawSeries(
            [.. memory.Select(r => Project(r.AtUtc, r.WorkingSetKb / 1024.0, memMinMb, memMaxMb))],
            AppColors.PerformanceMemoryBrush, MemoryStrokeWidth, fill: null);

        DrawSeries(
            [.. processor.Select(r => Project(r.AtUtc, r.Percent, 0, processorTop))],
            AppColors.PerformanceProcessorBrush, ProcessorStrokeWidth,
            fill: AppColors.PerformanceProcessorFillBrush);

        Point Project(DateTime atUtc, double value, double low, double high)
        {
            double x = (atUtc - startUtc) / span * w;
            double range = high - low;
            double y = range <= 0 ? h : h - (value - low) / range * h;
            return new Point(Math.Clamp(x, 0, w), Math.Clamp(y, 0, h));
        }
    }

    /// <summary>
    /// Draws one series as a monotone curve, optionally with a fade beneath it. A single point is
    /// drawn as a dot: a curve needs two, and at the slow end of the rate range a fresh window holds
    /// exactly one processor sample for the first ten seconds.
    /// </summary>
    private void DrawSeries(IReadOnlyList<Point> points, Brush stroke, double thickness, Brush? fill)
    {
        if (points.Count == 0) return;

        if (points.Count == 1)
        {
            var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = thickness * 2, Height = thickness * 2, Fill = stroke,
            };
            Canvas.SetLeft(dot, points[0].X - thickness);
            Canvas.SetTop(dot,  points[0].Y - thickness);
            PlotCanvas.Children.Add(dot);
            return;
        }

        var xs = new double[points.Count];
        var ys = new double[points.Count];
        for (int i = 0; i < points.Count; i++) { xs[i] = points[i].X; ys[i] = points[i].Y; }
        var tangents = MonotoneCubic.Tangents(xs, ys);

        if (fill is not null)
        {
            var fillGeometry = new PathGeometry();
            fillGeometry.Figures.Add(MonotonePath.Figure(points, tangents, closeAtY: PlotCanvas.ActualHeight));
            PlotCanvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = fillGeometry, Fill = fill });
        }

        var lineGeometry = new PathGeometry();
        lineGeometry.Figures.Add(MonotonePath.Figure(points, tangents));
        PlotCanvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Path
        {
            Data            = lineGeometry,
            Stroke          = stroke,
            StrokeThickness = thickness,
            StrokeLineJoin  = PenLineJoin.Round,
        });
    }

    private void UpdateStateLabel(int processorPoints, int memoryPoints)
    {
        PlotStateLabel.Text =
            !SettingsService.Current.PerformanceGraphEnabled ? "Measurement is off"
            : processorPoints == 0 && memoryPoints == 0      ? "Waiting for the first samples…"
                                                             : "";
    }

    private void UpdateCounts(IReadOnlyList<ResourceReading> memory)
    {
        CountsText.Text = memory.Count == 0
            ? ""
            : string.Create(CultureInfo.CurrentCulture,
                $"{memory[^1].Handles:N0} handles · {memory[^1].Threads:N0} threads");
    }

    private static string Percent(double value) =>
        string.Create(CultureInfo.CurrentCulture, $"{value:0.#} %");

    private static string Megabytes(double value) =>
        string.Create(CultureInfo.CurrentCulture, $"{value:0.#} MB");

    private static string FormatSpan(TimeSpan span) =>
        span.TotalMinutes >= 1
            ? string.Create(CultureInfo.CurrentCulture, $"−{span.TotalMinutes:0} min")
            : string.Create(CultureInfo.CurrentCulture, $"−{span.TotalSeconds:0} s");

    /// <summary>Rounds an axis top up to something a reader can divide in half in their head.</summary>
    private static double NiceCeiling(double value)
    {
        if (value <= 0) return MinProcessorAxisTop;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        foreach (double step in (double[])[1, 2, 2.5, 5, 10])
        {
            double candidate = step * magnitude;
            if (candidate >= value) return candidate;
        }
        return 10 * magnitude;
    }
}
