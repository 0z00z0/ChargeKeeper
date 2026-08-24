using System.Drawing;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ChargeKeeper.Helpers;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The "0z0 steel battery" mark exists three times over — brand\chargekeeper-icon.svg as the
/// vector, scripts\BatteryGlyph.ps1 for the two build-time generators, and Helpers\IconGenerator.cs
/// for the in-process tray icon. Nothing can share code across that divide, so these tests are what
/// stops the three drifting: a change to one and not the others goes red here instead of shipping
/// an executable whose Explorer icon no longer matches the brand it is drawn from.
/// <para>
/// IconGenerator states the mark's heights TWICE, on purpose: AppIconHeights is the brand's own
/// landscape shape, which the vector and the glyph script describe and every baked bitmap uses;
/// TraySlotHeights is the same mark stretched to fill a square 16 px notification-area slot. Both
/// are pinned below — the static set against the other two representations, the tray set against
/// its own literals — and a further test holds them apart, so re-merging them cannot pass quietly.
/// </para>
/// </summary>
public class BrandMarkGeometryTests
{
    // Coordinates are on the 256-unit reference canvas, so a unit is a unit in all three files and
    // a tolerance of a hundredth is far tighter than anything a rounding difference produces.
    private const double Tolerance = 0.01;

    /// <summary>The mark's figures, as one of the three representations states them.</summary>
    private sealed record MarkGeometry(
        double Canvas,
        double BodyX, double BodyY, double BodyW, double BodyH, double BodyRadius, double BodyPen,
        double CapX, double CapY, double CapW, double CapH, double CapRadius,
        double FillX, double FillY, double FillW, double FillH, double FillRadius, double FillAlpha,
        double GuardX, double GuardTop, double GuardBottom, double GuardPen)
    {
        internal double BodyRight  => BodyX + BodyW;
        internal double BodyBottom => BodyY + BodyH;
        internal double FillRight  => FillX + FillW;
        internal double CapCentreY => CapY + CapH / 2;
    }

    /// <summary>The three brand colours as the SVG spells them.</summary>
    private sealed record MarkColours(string Body, string Cap, string Fill, string Guard);

    // ── Locating the files ────────────────────────────────────────────────────

    /// <summary>Walks up from the test assembly to the repo root, probing for the marker file
    /// rather than hard-coding the output depth.</summary>
    private static string FindRepoFile(string relativePath)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate) && File.Exists(Path.Combine(dir.FullName, "ChargeKeeper.csproj")))
                return candidate;
        }

        throw new FileNotFoundException(
            $"Could not locate '{relativePath}' walking up from '{AppContext.BaseDirectory}'.");
    }

    // ── The vector ────────────────────────────────────────────────────────────

    private static XElement SvgRoot() => XDocument.Load(FindRepoFile(@"brand\chargekeeper-icon.svg")).Root!;

    private static double Attr(XElement e, string name)
    {
        string? raw = (string?)e.Attribute(name);
        Assert.False(raw is null, $"the SVG's <{e.Name.LocalName}> carries no {name} attribute.");
        return double.Parse(raw!, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Reads the mark out of the SVG. The three rects are told apart by what they are rather than by
    /// document order: the body is the only stroked outline, the charge fill the only shape carrying
    /// an opacity, and the cap what is left.
    /// </summary>
    private static MarkGeometry ReadSvg()
    {
        var svg = SvgRoot();
        var ns  = svg.Name.Namespace;

        var rects = svg.Elements(ns + "rect").ToList();
        Assert.Equal(3, rects.Count);

        var body = Assert.Single(rects, r => (string?)r.Attribute("fill") == "none");
        var fill = Assert.Single(rects, r => r.Attribute("opacity") is not null);
        var cap  = Assert.Single(rects.Except([body, fill]));
        var line = Assert.Single(svg.Elements(ns + "line"));

        // A viewBox that does not match width/height would silently rescale everything below it.
        var box = ((string)svg.Attribute("viewBox")!)
                  .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                  .Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(4, box.Length);
        Assert.Equal(0, box[0], Tolerance);
        Assert.Equal(0, box[1], Tolerance);
        Assert.Equal(Attr(svg, "width"),  box[2], Tolerance);
        Assert.Equal(Attr(svg, "height"), box[3], Tolerance);
        Assert.Equal(Attr(svg, "width"),  Attr(svg, "height"), Tolerance);

        // The guard line has to be vertical for a single x to describe it.
        Assert.Equal(Attr(line, "x1"), Attr(line, "x2"), Tolerance);

        return new MarkGeometry(
            Canvas:      Attr(svg, "width"),
            BodyX:       Attr(body, "x"),
            BodyY:       Attr(body, "y"),
            BodyW:       Attr(body, "width"),
            BodyH:       Attr(body, "height"),
            BodyRadius:  Attr(body, "rx"),
            BodyPen:     Attr(body, "stroke-width"),
            CapX:        Attr(cap, "x"),
            CapY:        Attr(cap, "y"),
            CapW:        Attr(cap, "width"),
            CapH:        Attr(cap, "height"),
            CapRadius:   Attr(cap, "rx"),
            FillX:       Attr(fill, "x"),
            FillY:       Attr(fill, "y"),
            FillW:       Attr(fill, "width"),
            FillH:       Attr(fill, "height"),
            FillRadius:  Attr(fill, "rx"),
            FillAlpha:   Math.Round(Attr(fill, "opacity") * 255),
            GuardX:      Attr(line, "x1"),
            GuardTop:    Attr(line, "y1"),
            GuardBottom: Attr(line, "y2"),
            GuardPen:    Attr(line, "stroke-width"));
    }

    private static MarkColours ReadSvgColours()
    {
        var svg = SvgRoot();
        var ns  = svg.Name.Namespace;

        var rects = svg.Elements(ns + "rect").ToList();
        var body  = Assert.Single(rects, r => (string?)r.Attribute("fill") == "none");
        var fill  = Assert.Single(rects, r => r.Attribute("opacity") is not null);
        var cap   = Assert.Single(rects.Except([body, fill]));
        var line  = Assert.Single(svg.Elements(ns + "line"));

        return new MarkColours(
            Body:  (string)body.Attribute("stroke")!,
            Cap:   (string)cap.Attribute("fill")!,
            Fill:  (string)fill.Attribute("fill")!,
            Guard: (string)line.Attribute("stroke")!);
    }

    // ── The PowerShell generator ──────────────────────────────────────────────

    // "Name = <number>", the shape BatteryGlyph.ps1's $BatteryGlyphGeometry block declares.
    private static readonly Regex GeometryEntry =
        new(@"^\s*(?<key>[A-Za-z]+)\s*=\s*(?<value>-?\d+(?:\.\d+)?)\s*$",
            RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>Reads the mark out of the geometry table in scripts\BatteryGlyph.ps1. Parsed rather
    /// than executed: running the script needs System.Drawing loaded into a PowerShell host, and the
    /// figures are the whole of what has to agree.</summary>
    private static MarkGeometry ReadGlyphScript()
    {
        string script = File.ReadAllText(FindRepoFile(@"scripts\BatteryGlyph.ps1"));

        int start = script.IndexOf("$BatteryGlyphGeometry = @{", StringComparison.Ordinal);
        Assert.True(start >= 0, @"scripts\BatteryGlyph.ps1 declares no $BatteryGlyphGeometry table.");
        int end = script.IndexOf("\n}", start, StringComparison.Ordinal);
        Assert.True(end > start, "the $BatteryGlyphGeometry table is not closed.");

        var values = GeometryEntry.Matches(script[start..end])
                                  .ToDictionary(m => m.Groups["key"].Value,
                                                m => double.Parse(m.Groups["value"].Value,
                                                                  CultureInfo.InvariantCulture));

        double Value(string key)
        {
            Assert.True(values.ContainsKey(key), $"$BatteryGlyphGeometry has no {key} entry.");
            return values[key];
        }

        return new MarkGeometry(
            Canvas:      Value("Canvas"),
            BodyX:       Value("BodyX"),
            BodyY:       Value("BodyY"),
            BodyW:       Value("BodyW"),
            BodyH:       Value("BodyH"),
            BodyRadius:  Value("BodyRadius"),
            BodyPen:     Value("BodyPen"),
            CapX:        Value("CapX"),
            CapY:        Value("CapY"),
            CapW:        Value("CapW"),
            CapH:        Value("CapH"),
            CapRadius:   Value("CapRadius"),
            FillX:       Value("FillX"),
            FillY:       Value("FillY"),
            FillW:       Value("FillW"),
            FillH:       Value("FillH"),
            FillRadius:  Value("FillRadius"),
            FillAlpha:   Value("FillAlpha"),
            GuardX:      Value("GuardX"),
            GuardTop:    Value("GuardTop"),
            GuardBottom: Value("GuardBottom"),
            GuardPen:    Value("GuardPen"));
    }

    // ── Vector vs generator ───────────────────────────────────────────────────

    [Fact]
    public void TheGlyphScriptStatesTheSameMarkAsTheVector()
    {
        // One assertion over the whole record: a mismatch on any single figure names both values.
        Assert.Equal(ReadSvg(), ReadGlyphScript());
    }

    // ── Vector vs the runtime renderer's declared figures ─────────────────────

    [Fact]
    public void TheVectorUsesTheRenderersReferenceCanvas() =>
        Assert.Equal(IconGenerator.MarkCanvas, ReadSvg().Canvas, Tolerance);

    [Fact]
    public void TheVectorStatesTheSameHeightsAsTheStaticGeometrySet()
    {
        // The whole of AppIconHeights against the whole of the vector, in one place: body, cap,
        // interior band and the guard line's ink extent. Body and cap are private constants in the
        // renderer no longer — they are this record — so this is a direct pin rather than a probe.
        var svg    = ReadSvg();
        var appIcon = IconGenerator.AppIconHeights;

        Assert.Equal(svg.BodyY,       appIcon.BodyTop,        Tolerance);
        Assert.Equal(svg.BodyBottom,  appIcon.BodyBottom,     Tolerance);
        Assert.Equal(svg.CapY,        appIcon.CapTop,         Tolerance);
        Assert.Equal(svg.CapY + svg.CapH, appIcon.CapBottom,  Tolerance);
        Assert.Equal(svg.FillY,       appIcon.InteriorTop,    Tolerance);
        Assert.Equal(svg.FillY + svg.FillH, appIcon.InteriorBottom, Tolerance);
        Assert.Equal(svg.GuardTop,    appIcon.InkTop,         Tolerance);
        Assert.Equal(svg.GuardBottom, appIcon.InkBottom,      Tolerance);
    }

    [Fact]
    public void TheTraySlotSetKeepsTheMaximisedHeights()
    {
        // #112: the notification-area slot is square and as little as 16 px across, so the tray icon
        // gets the brand mark's vertical figures scaled 1.6x about y = 128. Pinned to its own
        // literals rather than derived from the vector — the vector deliberately does NOT say this.
        var tray = IconGenerator.TraySlotHeights;

        Assert.Equal(51.0,  tray.BodyTop,        Tolerance);
        Assert.Equal(205.0, tray.BodyBottom,     Tolerance);
        Assert.Equal(93.0,  tray.CapTop,         Tolerance);
        Assert.Equal(163.0, tray.CapBottom,      Tolerance);
        Assert.Equal(72.0,  tray.InteriorTop,    Tolerance);
        Assert.Equal(185.0, tray.InteriorBottom, Tolerance);
        Assert.Equal(29.0,  tray.InkTop,         Tolerance);
        Assert.Equal(227.0, tray.InkBottom,      Tolerance);
    }

    [Fact]
    public void TheTwoHeightSetsStayApart()
    {
        // The failure this exists for is a re-merge: assigning one set to the other, or propagating
        // the tray's figures back into the vector, which is what made the installer and About icons
        // chubby the first time. Every tray figure is strictly outside its static counterpart.
        var appIcon = IconGenerator.AppIconHeights;
        var tray    = IconGenerator.TraySlotHeights;

        Assert.True(tray.BodyTop        < appIcon.BodyTop,        "the tray body is no taller than the brand's.");
        Assert.True(tray.BodyBottom     > appIcon.BodyBottom,     "the tray body is no taller than the brand's.");
        Assert.True(tray.CapTop         < appIcon.CapTop,         "the tray cap is no taller than the brand's.");
        Assert.True(tray.CapBottom      > appIcon.CapBottom,      "the tray cap is no taller than the brand's.");
        Assert.True(tray.InteriorTop    < appIcon.InteriorTop,    "the tray interior is no taller than the brand's.");
        Assert.True(tray.InteriorBottom > appIcon.InteriorBottom, "the tray interior is no taller than the brand's.");
        Assert.True(tray.InkTop         < appIcon.InkTop,         "the tray ink is no taller than the brand's.");
        Assert.True(tray.InkBottom      > appIcon.InkBottom,      "the tray ink is no taller than the brand's.");

        // Both sets stay on the canvas's centre line, so the two marks differ in height and in
        // nothing else.
        Assert.Equal(IconGenerator.MarkCanvas / 2f, (appIcon.InkTop + appIcon.InkBottom) / 2f, 1.0);
        Assert.Equal(IconGenerator.MarkCanvas / 2f, (tray.InkTop    + tray.InkBottom)    / 2f, 1.0);
    }

    [Fact]
    public void TheVectorsGuardLineSitsAtTheCanonicalGuardPercent()
    {
        // The static .ico is the live renderer fed MarkCanonicalGuard; the vector draws the line at a
        // fixed x. They agree only while that x is where the interior band puts that percentage.
        Assert.Equal(IconGenerator.MarkInteriorX(IconGenerator.MarkCanonicalGuard),
                     ReadSvg().GuardX, Tolerance);
    }

    [Fact]
    public void TheVectorsChargeFillEndsAtTheCanonicalPercent() =>
        Assert.Equal(IconGenerator.MarkInteriorX(IconGenerator.MarkCanonicalPercent),
                     ReadSvg().FillRight, Tolerance);

    [Fact]
    public void TheCanonicalPercentLandsInTheTierTheVectorIsPaintedIn()
    {
        // The vector's fill is sage. If MarkCanonicalPercent ever drops to the amber tier the static
        // icon changes colour while the vector does not, which no coordinate check would catch.
        Assert.True(IconGenerator.MarkCanonicalPercent > GaugePalette.GreenAbovePct,
                    $"the canonical {IconGenerator.MarkCanonicalPercent} % is at or below the "
                    + $"{GaugePalette.GreenAbovePct} % green threshold, so the mark renders amber, "
                    + "not the sage the vector is drawn in.");
    }

    [Fact]
    public void TheVectorUsesTheGaugePalette()
    {
        var colours = ReadSvgColours();
        Assert.Equal(Hex(GaugePalette.SteelBlue),  colours.Body,  ignoreCase: true);
        Assert.Equal(Hex(GaugePalette.SteelBlue),  colours.Cap,   ignoreCase: true);
        Assert.Equal(Hex(GaugePalette.SageGreen),  colours.Fill,  ignoreCase: true);
        Assert.Equal(Hex(GaugePalette.Terracotta), colours.Guard, ignoreCase: true);
    }

    private static string Hex(uint packedArgb) => $"#{packedArgb & 0x00FFFFFF:X6}";

    // ── Vector vs the pixels the static renderer actually produces ────────────
    //
    // The declared figures agreeing is not the same as the render obeying them. These probe the
    // bitmap the static .ico is written from, at the reference canvas size, where the vector says
    // each shape is. A tray-set render substituted here would fail every one of them.

    private static Bitmap RenderCanonicalMark() =>
        IconGenerator.RenderAppIconBitmap((int)IconGenerator.MarkCanvas);

    [Fact]
    public void TheRenderedMarkHasItsBodyWhereTheVectorDrawsIt()
    {
        var svg = ReadSvg();
        using var bmp = RenderCanonicalMark();

        // Mid-span of the top and bottom edges, on the stroke's centre line. Left of the guard line
        // so its halo cannot be what is being measured.
        double x = (svg.BodyX + svg.GuardX) / 2;
        AssertOpaque(bmp, x, svg.BodyY,      GaugePalette.SteelBlue, "the body's top edge");
        AssertOpaque(bmp, x, svg.BodyBottom, GaugePalette.SteelBlue, "the body's bottom edge");

        // Just outside the stroke's outer edge there must be nothing — a taller body would show here.
        AssertTransparent(bmp, x, svg.BodyY      - svg.BodyPen, "above the body");
        AssertTransparent(bmp, x, svg.BodyBottom + svg.BodyPen, "below the body");
    }

    [Fact]
    public void TheRenderedMarkHasItsCapWhereTheVectorDrawsIt()
    {
        var svg = ReadSvg();
        using var bmp = RenderCanonicalMark();

        double x = svg.CapX + svg.CapW / 2;
        AssertOpaque(bmp, x, svg.CapCentreY,   GaugePalette.SteelBlue, "the cap");
        AssertOpaque(bmp, x, svg.CapY      + 3, GaugePalette.SteelBlue, "the cap's top");
        AssertOpaque(bmp, x, svg.CapY + svg.CapH - 3, GaugePalette.SteelBlue, "the cap's bottom");

        AssertTransparent(bmp, x, svg.CapY - 6,               "above the cap");
        AssertTransparent(bmp, x, svg.CapY + svg.CapH + 6,    "below the cap");
    }

    [Fact]
    public void TheRenderedMarkHasItsChargeFillWhereTheVectorDrawsIt()
    {
        var svg = ReadSvg();
        using var bmp = RenderCanonicalMark();

        // Well left of the guard line's halo, which is laid over the fill's right end.
        double x = (svg.FillX + svg.GuardX) / 2 - 20;
        AssertColour(bmp, x, svg.FillY + 4,               GaugePalette.SageGreen, (int)svg.FillAlpha,
                     "the fill's top");
        AssertColour(bmp, x, svg.FillY + svg.FillH - 4,   GaugePalette.SageGreen, (int)svg.FillAlpha,
                     "the fill's bottom");

        // Between the body's inner edge and the fill's, the battery is empty.
        AssertTransparent(bmp, x, svg.FillY - 6,             "above the fill, inside the body");
        AssertTransparent(bmp, x, svg.FillY + svg.FillH + 6, "below the fill, inside the body");

        // And to the right of the guard line the body is empty too — the fill stops at 76 %.
        AssertTransparent(bmp, svg.BodyRight - svg.BodyPen / 2 - 10, svg.CapCentreY,
                          "right of the guard line, inside the body");
    }

    [Fact]
    public void TheRenderedMarksGuardLineOverhangsTheBodyExactlyAsTheVectorDoes()
    {
        var svg = ReadSvg();
        using var bmp = RenderCanonicalMark();

        // Between the ink top and the body's outer edge: the guard line and nothing else.
        double aboveBody = (svg.GuardTop + (svg.BodyY - svg.BodyPen / 2)) / 2;
        double belowBody = (svg.GuardBottom + (svg.BodyBottom + svg.BodyPen / 2)) / 2;
        AssertOpaque(bmp, svg.GuardX, aboveBody, GaugePalette.Terracotta, "the guard line above the body");
        AssertOpaque(bmp, svg.GuardX, belowBody, GaugePalette.Terracotta, "the guard line below the body");

        // Past the declared ink extent nothing is drawn, halo included.
        AssertTransparent(bmp, svg.GuardX, svg.GuardTop    - 6, "above the mark's ink");
        AssertTransparent(bmp, svg.GuardX, svg.GuardBottom + 6, "below the mark's ink");
    }

    // ── Pixel helpers ─────────────────────────────────────────────────────────

    private static void AssertOpaque(Bitmap bmp, double x, double y, uint expected, string what) =>
        AssertColour(bmp, x, y, expected, 255, what);

    // GDI+ composites the semi-transparent fill onto a cleared bitmap with its own rounding, so a
    // channel may land one off the brush's own value. Two is loose enough to survive that and far
    // tighter than any change of tint or tier.
    private const int ChannelTolerance = 2;

    private static void AssertColour(Bitmap bmp, double x, double y, uint expected, int alpha, string what)
    {
        var actual = Probe(bmp, x, y);
        string where = $"{what} at ({x:0.##}, {y:0.##})";

        Assert.True(Math.Abs(actual.A - alpha) <= ChannelTolerance,
                    $"{where} has alpha {actual.A}, expected {alpha}.");
        Assert.True(Near(actual.R, expected >> 16) && Near(actual.G, expected >> 8) && Near(actual.B, expected),
                    $"{where} is {actual.R:X2}{actual.G:X2}{actual.B:X2}, expected {expected & 0x00FFFFFF:X6}.");

        static bool Near(byte actual, uint expected) => Math.Abs(actual - (byte)expected) <= ChannelTolerance;
    }

    private static void AssertTransparent(Bitmap bmp, double x, double y, string what)
    {
        var actual = Probe(bmp, x, y);
        Assert.True(actual.A == 0,
                    $"{what} at ({x:0.##}, {y:0.##}) carries ink (alpha {actual.A}).");
    }

    private static Color Probe(Bitmap bmp, double x, double y)
    {
        int px = (int)Math.Round(x), py = (int)Math.Round(y);
        Assert.InRange(px, 0, bmp.Width  - 1);
        Assert.InRange(py, 0, bmp.Height - 1);
        return bmp.GetPixel(px, py);
    }
}
