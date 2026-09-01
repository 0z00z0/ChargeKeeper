using System.Text.RegularExpressions;
using ChargeKeeper.Helpers;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The charge line's gradient, asserted over a recorded history window rather than made-up points:
/// synthetic samples pass whatever the renderer does with them, and did while the shipped line was
/// drawn in one colour. The fixture is a real six-hour window straddling four power-state stretches.
/// </summary>
public class GraphLineGradientTests
{
    private const uint Accent = GaugePalette.SteelBlue;
    private const int  MaxStops = 200;

    /// <summary>The recorded window, parsed by the same reader the app uses.</summary>
    private static List<BatterySample> RealWindow()
    {
        var path = RepoFiles.Find(Path.Combine("Tests", "Fixtures", "battery-level-history-6h.csv"));
        var samples = new List<BatterySample>();
        foreach (var line in File.ReadLines(path))
            if (BatteryHistoryService.TryParse(line, out var s))
                samples.Add(s);
        return samples;
    }

    private static int[] AllIndices(int count) => [.. Enumerable.Range(0, count)];

    /// <summary>X positions as the plot builds them for a gap-free window: proportional to elapsed
    /// time across the plot width, inset by the same padding the canvas uses.</summary>
    private static double[] TimeProportionalX(IReadOnlyList<BatterySample> samples, double width, double pad)
    {
        double span = (samples[^1].AtUtc - samples[0].AtUtc).Ticks;
        var xs = new double[samples.Count];
        for (int i = 0; i < samples.Count; i++)
            xs[i] = pad + (samples[i].AtUtc - samples[0].AtUtc).Ticks / span * (width - pad * 2);
        return xs;
    }

    [Fact]
    public void TheFixtureIsARealWindowCarryingMoreThanOnePowerState()
    {
        var samples = RealWindow();
        Assert.True(samples.Count > 500, $"Fixture holds only {samples.Count} samples.");

        var recorded = samples.Where(s => s.State is not null).Select(s => s.State!.Value).Distinct().ToList();
        Assert.Contains(PowerState.Charging, recorded);
        Assert.Contains(PowerState.Discharging, recorded);
        // The window starts before the state was ever written, so the fallback branch is exercised too.
        Assert.Contains(samples, s => s.State is null);
    }

    [Fact]
    public void OverTheRecordedWindow_TheLineTakesMoreThanOneColour()
    {
        var samples = RealWindow();
        var xs      = TimeProportionalX(samples, width: 900, pad: 4);

        var stops = GraphColouring.LineStops(
            GraphLineColouring.ByLevelAndState, samples, xs, AllIndices(samples.Count), Accent, MaxStops);

        var colours = stops.Select(s => s.Argb).Distinct().ToList();
        Assert.True(colours.Count > 1,
                    $"The line is drawn in a single colour over real history: 0x{colours[0]:X8}.");
        Assert.Contains(colours, c => c != Accent);
    }

    [Fact]
    public void OverTheRecordedWindow_AStretchOnBatteryIsNotDrawnInTheAccent()
    {
        var samples = RealWindow();
        var xs      = TimeProportionalX(samples, width: 900, pad: 4);

        // Every recorded sample, not a stride of them, so the assertion cannot be an artefact of which
        // points a cap happened to keep.
        var stops = GraphColouring.LineStops(
            GraphLineColouring.ByLevelAndState, samples, xs, AllIndices(samples.Count), Accent,
            maxStops: samples.Count);

        int onBattery = 0;
        for (int i = 0; i < samples.Count; i++)
            if (samples[i].State == PowerState.Discharging && stops[i].Argb != Accent) onBattery++;

        Assert.True(onBattery > 50,
                    $"Only {onBattery} on-battery points differ from the accent; the on-battery " +
                    "stretches of a recorded window are indistinguishable from an uncoloured line.");
    }

    [Fact]
    public void StopsRunLeftToRightAndCoverTheWholeRun()
    {
        var samples = RealWindow();
        var xs      = TimeProportionalX(samples, width: 900, pad: 4);

        var stops = GraphColouring.LineStops(
            GraphLineColouring.ByLevelAndState, samples, xs, AllIndices(samples.Count), Accent, MaxStops);

        // A relative-mapped brush spreads 0–1 across the path it paints, so the run's own ends are
        // where the gradient has to start and stop. Anything narrower leaves part of the stroke on an
        // end stop's colour; anything wider crops the middle out of view.
        Assert.Equal(0.0, stops[0].Offset, 6);
        Assert.Equal(1.0, stops[^1].Offset, 6);
        for (int i = 1; i < stops.Count; i++)
            Assert.True(stops[i].Offset >= stops[i - 1].Offset,
                        $"Stop {i} steps backwards: {stops[i].Offset} after {stops[i - 1].Offset}.");
    }

    [Fact]
    public void ARunThatIsNotTheWholeSeriesIsNormalisedToItself()
    {
        var samples = RealWindow();
        var xs      = TimeProportionalX(samples, width: 900, pad: 4);
        // A middle slice, as a run after a downtime break would be.
        int[] slice = [.. Enumerable.Range(samples.Count / 3, samples.Count / 3)];

        var stops = GraphColouring.LineStops(
            GraphLineColouring.ByLevelAndState, samples, xs, slice, Accent, MaxStops);

        Assert.Equal(0.0, stops[0].Offset, 6);
        Assert.Equal(1.0, stops[^1].Offset, 6);
    }

    [Fact]
    public void OneColour_ProducesNoVaryingGradientAtAll()
    {
        var samples = RealWindow();
        var xs      = TimeProportionalX(samples, width: 900, pad: 4);

        Assert.False(GraphColouring.VariesByPoint(GraphLineColouring.OneColour));

        var stops = GraphColouring.LineStops(
            GraphLineColouring.OneColour, samples, xs, AllIndices(samples.Count), Accent, MaxStops);
        Assert.All(stops, s => Assert.Equal(Accent, s.Argb));
    }

    [Fact]
    public void AnEmptyRunProducesNoStops() =>
        Assert.Empty(GraphColouring.LineStops(
            GraphLineColouring.ByLevelAndState, RealWindow(), [0.0], [], Accent, MaxStops));

    /// <summary>
    /// Pins the defect that left the shipped line in one colour: the gradient was mapped in absolute
    /// coordinates over the plot width while painting a path whose own bounding box is the stroke, so
    /// the visible stroke sat on the first stop. Every gradient in this app that renders is mapped
    /// relative to what it paints, and none names <c>BrushMappingMode</c>.
    /// </summary>
    [Fact]
    public void NoGradientInTheAppIsMappedInAbsoluteCoordinates()
    {
        foreach (var relative in new[]
                 {
                     Path.Combine("UI", "BatteryHistoryGraphControl.xaml.cs"),
                     Path.Combine("Helpers", "AppColors.cs"),
                 })
        {
            string source = File.ReadAllText(RepoFiles.Find(relative));
            Assert.DoesNotContain("BrushMappingMode", source, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// One definition, two consumers: the tray icon and the graph must not each decide what a level
    /// and a state look like. The graph reaches the palette only through
    /// <see cref="GraphColouring"/>, so the control naming <see cref="GaugePalette"/> at all — or
    /// restating a scale anchor — would be a second implementation.
    /// </summary>
    [Fact]
    public void TheGraphControlDoesNotDecideColoursOfItsOwn()
    {
        string source = File.ReadAllText(
            RepoFiles.Find(Path.Combine("UI", "BatteryHistoryGraphControl.xaml.cs")));

        Assert.DoesNotContain("GaugePalette", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PowerState.", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// The scale anchors are named in exactly one file. A copy elsewhere — a graph-only override, a
    /// "temporary" second table — trips this without the test having to know what the anchors are.
    /// </summary>
    [Fact]
    public void TheGaugeScalesAreDeclaredInOnePlaceOnly()
    {
        string root = Path.GetDirectoryName(RepoFiles.Find("ChargeKeeper.csproj"))!;
        var declaring = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file);
            var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            // Build output and the suite itself are not shipped source.
            if (segments.Any(s => s.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                                  s.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                                  s.Equals("Tests", StringComparison.OrdinalIgnoreCase))) continue;

            // A scale is a list of GaugeStop anchors; anywhere the type is named is a scale table.
            // Whole word: a XAML element called GaugeStopTick is not an anchor.
            if (Regex.IsMatch(File.ReadAllText(file), @"\bGaugeStop\b"))
                declaring.Add(relative);
        }

        Assert.Equal([Path.Combine("Helpers", "GaugePalette.cs")], declaring);
    }
}
