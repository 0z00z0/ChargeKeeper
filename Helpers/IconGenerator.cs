using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using ChargeKeeper.Services;
using ChargeKeeper.Vendors;

namespace ChargeKeeper.Helpers;

/// <summary>
/// Renders the ChargeKeeper tray icon: the "0z0 steel battery" seed mark, written once to a
/// multi-size .ico on disk, and the live battery icon — arc, numeric or the mark itself — built in
/// memory per state change. Both carry the maximised tray geometry, so nothing the notification
/// area shows changes shape between the seed and the first reading.
/// The on-disk file lets H.NotifyIcon reload the icon if it is recreated, and avoids the GDI handle
/// leak <c>Bitmap.GetHicon()</c> introduces.
/// The mark in the brand's own classic proportions is <see cref="RenderAppIconBitmap"/>, for the
/// in-window chrome; that one never reaches the tray.
/// </summary>
internal static class IconGenerator
{
    // Rounded-square background geometry shared by the numeric icon renderer.
    private const float CornerRadiusFraction = 0.18f;
    private const float MarginFraction        = 0.04f; // gap from icon edge to background square

    // Sizes baked into the static on-disk .ico — 100/125/150/200 % tray DPI without upscaling.
    private static readonly int[] IconSizes = [32, 24, 20, 16];

    // Logical small-icon size in px at 96 DPI, scaled by the taskbar DPI for the physical slot.
    private const int LogicalSmallIconSize = 16;

    /// <summary>Physical pixel size for the live tray icon at monitor <paramref name="dpi"/>: 16 px
    /// logical scaled to that DPI, clamped to 16..64 (100 %..400 %) so a bogus DPI can never yield a
    /// giant or empty bitmap.</summary>
    internal static int SlotSizeForDpi(uint dpi)
    {
        // 0 means "unknown" from the Win32 query; 96 keeps the true logical size rather than the floor.
        if (dpi == 0) dpi = 96;
        int size = (int)Math.Round(LogicalSmallIconSize * dpi / 96.0, MidpointRounding.AwayFromZero);
        return Math.Clamp(size, 16, 64);
    }

    /// <summary>The tray slot size the shell will display, sized to the TASKBAR's DPI rather than the
    /// process's DPI context — the two differ when the taskbar sits on a secondary monitor at another
    /// scale, and rendering for the wrong one washes out the thin arc stroke.</summary>
    private static int CurrentTraySlotSize() =>
        _cachedSlotSize ??= SlotSizeForDpi(NativeMethods.GetTaskbarDpi());

    // The taskbar DPI only changes on a display event, never between two battery ticks, so the
    // FindWindow + GetDpiForWindow round-trips are cached rather than repeated per repaint.
    private static int? _cachedSlotSize;

    /// <summary>Drops the cached tray-slot size so the next render re-queries the taskbar DPI. Call
    /// when the display configuration changes.</summary>
    internal static void InvalidateSlotSizeCache() => _cachedSlotSize = null;

    // Taskbar theme, cached alongside the slot size for the same reason: it is display state, not
    // something that moves between two battery ticks.
    private static bool? _cachedLightTaskbar;

    /// <summary>Whether the taskbar is painted light, from the shell's own setting rather than
    /// guessed from a colour.</summary>
    internal static bool TaskbarUsesLightTheme() => _cachedLightTaskbar ??= ReadSystemUsesLightTheme();

    /// <summary>Drops the cached taskbar theme so the next render re-reads it.</summary>
    internal static void InvalidateThemeCache() => _cachedLightTaskbar = null;

    /// <summary>Whether a <c>UserPreferenceChanged</c> category can be carrying a taskbar light/dark
    /// flip. The toggle broadcasts WM_SETTINGCHANGE "ImmersiveColorSet", which maps to no SPI code
    /// and so falls to <c>General</c>; the same switch also produces WM_SYSCOLORCHANGE
    /// (<c>Color</c>) and WM_THEMECHANGED (<c>VisualStyle</c>). None of the three is exclusive to a
    /// theme change, so a caller still has to check the value — see
    /// <see cref="RefreshThemeCacheIfChanged"/>.</summary>
    internal static bool CategoryCanCarryThemeChange(Microsoft.Win32.UserPreferenceCategory category) =>
        category is Microsoft.Win32.UserPreferenceCategory.General
                 or Microsoft.Win32.UserPreferenceCategory.Color
                 or Microsoft.Win32.UserPreferenceCategory.VisualStyle;

    /// <summary>Re-reads the taskbar theme and reports whether it moved. The notification carrying a
    /// light/dark flip is a catch-all category that fires for unrelated settings too, so the caller
    /// gates its repaint on the value having changed rather than on the event arriving. A cold cache
    /// counts as changed: the previous value is unknown, and one repaint is the safe answer.</summary>
    internal static bool RefreshThemeCacheIfChanged()
    {
        bool light = ReadSystemUsesLightTheme();
        if (_cachedLightTaskbar == light) return false;
        _cachedLightTaskbar = light;
        return true;
    }

    private static bool ReadSystemUsesLightTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int light && light != 0;
        }
        catch
        {
            // A missing or unreadable key is not worth failing a repaint over; dark is both the
            // Windows default and the background the fixed palette was chosen against.
            return false;
        }
    }

    // Brand-mark palette, read from GaugePalette so the tray icon and the dashboard cannot drift.
    // The GEOMETRY is not shared that way — see RenderMarkBitmap. The interior fill is not here: it
    // is the charge tier, from FillFor, like every other style's.
    private static readonly Color MarkSteel      = FromPacked(GaugePalette.SteelBlue);  // body outline + cap
    private static readonly Color MarkTerracotta = FromPacked(GaugePalette.Terracotta); // guard line

    /// <summary>The brand mark's own interior colour, fixed. The canonical renders draw a level that
    /// never moves, so they take the brand's sage rather than sampling a live gauge scale that would
    /// drift the mark away from brand\chargekeeper-icon.svg.</summary>
    private static readonly Color MarkSage = FromPacked(GaugePalette.SageGreen);

    private static Color FromPacked(uint argb) => Color.FromArgb(unchecked((int)argb));

    /// <summary>
    /// How hard the glyph's edge has to be drawn, and how visible the arc's empty track is. The tier
    /// colours all sit mid-luminance, so one setting cannot serve both taskbars: on a dark taskbar
    /// the background does the separating and a soft shadow is enough, on a light one the rim is the
    /// only thing there is.
    /// </summary>
    internal readonly record struct IconContrast(
        Color Outline, float OutlineFraction, float OutlineFloor, Color Track)
    {
        internal static IconContrast For(bool lightTaskbar) => lightTaskbar
            ? new(Color.FromArgb(190, 0, 0, 0), 0.09f, 2.0f, Color.FromArgb(150,  90,  90,  90))
            : new(Color.FromArgb( 90, 0, 0, 0), 0.06f, 1.5f, Color.FromArgb(160, 140, 140, 140));

        /// <summary>How much wider than the stroke it sits under the halo is drawn, at
        /// <paramref name="size"/> px. Floored so it survives the 16 px frame.</summary>
        internal float ExtraWidth(float size) => Math.Max(OutlineFloor, size * OutlineFraction);
    }

    /// <summary>The contrast for the taskbar as it is painted now.</summary>
    private static IconContrast CurrentContrast() => IconContrast.For(TaskbarUsesLightTheme());

    // Shared by every tray renderer, and by the dashboard gauge through GaugePalette itself, so no
    // two surfaces can drift on the scales.
    private static Color FillFor(int percent, PowerState state) =>
        FromPacked(GaugePalette.FillFor(percent, state));

    // Stamped into the filename so an in-place app update regenerates the icon rather than serving
    // the previous version's cached file. Bump on any change to the mark.
    private const string IconVersion = "v10";

    /// <summary>Generates the multi-size ICO file the tray is seeded from and returns its path, or
    /// returns the cached path when it already exists. The file is the notification area's alone —
    /// it carries <see cref="TraySlotHeights"/>, so the seed and every later repaint are the same
    /// shape, and a shell-driven reload of the file before the first battery event stays correct
    /// too.</summary>
    internal static string GenerateAndSaveTrayIcon(string outputDirectory)
    {
        var icoPath = Path.Combine(outputDirectory, $"ChargeKeeper-{IconVersion}.ico");
        // A zero-length file is a killed launch's leftover, not a cached icon.
        if (File.Exists(icoPath) && new FileInfo(icoPath).Length > 0) return icoPath;

        SaveAsIco(icoPath);
        return icoPath;
    }

    /// <summary>
    /// Renders the live battery icon as a single-frame <see cref="System.Drawing.Icon"/> at the
    /// current tray-slot size, in the style <paramref name="mode"/> selects. The returned icon owns
    /// an independent, data-backed handle, so the caller may dispose it once a newer icon replaces it.
    /// <paramref name="threshold"/> adds the start and stop marks; null draws none.
    /// </summary>
    internal static System.Drawing.Icon RenderBatteryIcon(
        int percent, PowerState state, TrayIconMode mode = TrayIconMode.Arc,
        ChargeThresholdState? threshold = null)
    {
        Bitmap Render(int size) => RenderStyleBitmap(size, percent, state, mode, threshold);

        using var ms = new MemoryStream();
        WriteIco(ms, Render, [CurrentTraySlotSize()]);
        ms.Position = 0;
        return new System.Drawing.Icon(ms);
    }

    /// <summary>One frame of the selected style at <paramref name="size"/> px. Split out from
    /// <see cref="RenderBatteryIcon"/> so a caller can render a known size rather than whatever the
    /// live tray slot happens to be.</summary>
    internal static Bitmap RenderStyleBitmap(int size, int percent, PowerState state, TrayIconMode mode,
                                             ChargeThresholdState? threshold = null) =>
        mode switch
        {
            TrayIconMode.Numeric   => RenderNumericBitmap(size, percent, state),
            TrayIconMode.BrandMark => RenderMarkBitmap(size, percent, FillFor(percent, state), threshold,
                                                      TraySlotHeights),
            _                      => RenderBatteryBitmap(size, percent, state, threshold),
        };

    /// <summary>
    /// Which threshold marks the icon carries. Nothing at all unless the firmware is actually
    /// capping the charge, and the start mark separately: HP and Surface report Start = 0 by
    /// contract, so a start mark can never be assumed from a stop one.
    /// </summary>
    internal static (int? Stop, int? Start) ThresholdMarksFor(ChargeThresholdState? state) =>
        state is null || !state.IsLimiting
            ? (null, null)
            : (state.Stop, state.HasStartThreshold ? state.Start : null);

    /// <summary>Renders the percentage as a large number on a colour-coded rounded square.</summary>
    private static Bitmap RenderNumericBitmap(int size, int percent, PowerState state)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode   = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        Color bg = FillFor(percent, state);

        int margin = Math.Max(1, (int)Math.Round(size * MarginFraction));
        var rect   = new Rectangle(margin, margin, size - margin * 2 - 1, size - margin * 2 - 1);
        int radius = Math.Max(2, (int)Math.Round(size * CornerRadiusFraction));
        using (var bgBrush = new SolidBrush(bg))
        using (var path    = BuildRoundedRectPath(rect, radius))
            g.FillPath(bgBrush, path);

        // Three-digit "100" is scaled down so it still fits the slot.
        string label  = percent > 0 ? $"{percent}" : "?";
        float  emSize = size * (label.Length >= 3 ? 0.46f : 0.66f);
        using var sf  = new StringFormat
        {
            Alignment     = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags   = StringFormatFlags.NoWrap,
            Trimming      = StringTrimming.None,
        };

        // Dark outline under the white fill: plain white is invisible on the light-green background.
        using var family = new FontFamily("Segoe UI");
        using var gp     = new GraphicsPath();
        gp.AddString(label, family, (int)System.Drawing.FontStyle.Bold, emSize,
                     new RectangleF(0, -size * 0.04f, size, size), sf);
        using (var outline = new System.Drawing.Pen(Color.FromArgb(215, 0, 0, 0), Math.Max(2f, size * 0.10f))
               { LineJoin = LineJoin.Round })
            g.DrawPath(outline, gp);
        using (var fill = new SolidBrush(Color.White))
            g.FillPath(fill, gp);

        return bmp;
    }

    // Brand-mark geometry on the 256-unit reference canvas. The interior band is where the charge
    // fill lives: 0 % is its left edge, 100 % its right, and the guard line sits at a percentage on
    // the same scale. Horizontal figures, radii, pen widths and colours are the same wherever the
    // mark is drawn; the heights are not, and MarkHeights is that difference.
    private const float MarkInteriorLeft  = 36f;
    private const float MarkInteriorRight = 185f;

    /// <summary>The reference canvas the mark's figures are expressed on.</summary>
    internal const float MarkCanvas = 256f;

    /// <summary>The mark's vertical figures, all centred on y = 128. The guard line overhangs the
    /// body top and bottom, so its extent is the mark's full ink height.</summary>
    internal readonly record struct MarkHeights(
        float BodyTop, float BodyBottom,
        float CapTop,  float CapBottom,
        float InteriorTop, float InteriorBottom,
        float InkTop,  float InkBottom);

    // TWO SETS, DELIBERATELY. The surfaces are shaped differently and one set cannot serve both, so
    // re-merging them regresses whichever one loses. #112 asked for the tray slot alone.

    /// <summary>The mark in the brand's own proportions, as brand\chargekeeper-icon.svg draws it:
    /// landscape, its ink over 48 % of the canvas height. This is what the static on-disk .ico, the
    /// app and setup icons and the wizard banners are drawn on — surfaces whose chrome leaves the
    /// mark room around it, and where stretching it to the frame reads as a chubby battery.</summary>
    internal static readonly MarkHeights AppIconHeights = new(
        BodyTop:      80f, BodyBottom:     176f,
        CapTop:      106f, CapBottom:      150f,
        InteriorTop: 101f, InteriorBottom: 156f,
        InkTop:       66f, InkBottom:      190f);

    /// <summary>The same mark with its vertical figures scaled 1.6x about the centre line, taking
    /// the ink to 77 % of the canvas height. The live tray icon only: the notification area's slot
    /// is square and as little as 16 px across, so the brand's landscape proportions letterbox away
    /// half of it.</summary>
    internal static readonly MarkHeights TraySlotHeights = new(
        BodyTop:      51f, BodyBottom:     205f,
        CapTop:       93f, CapBottom:      163f,
        InteriorTop:  72f, InteriorBottom: 185f,
        InkTop:       29f, InkBottom:      227f);

    // The charge level and guard position that reproduce brand\chargekeeper-icon.svg's fixed fill
    // rect and guard line. Geometry only: the canonical renders take MarkSage, so this level no
    // longer decides the mark's colour.
    internal const int MarkCanonicalPercent = 76;
    internal const int MarkCanonicalGuard   = 84;

    /// <summary>The x on the reference canvas where <paramref name="percent"/> falls in the mark's
    /// interior band.</summary>
    internal static float MarkInteriorX(int percent) =>
        MarkInteriorLeft + (MarkInteriorRight - MarkInteriorLeft) * Math.Clamp(percent, 0, 100) / 100f;

    /// <summary>
    /// Renders the "0z0 steel battery" mark on a transparent background: a SteelBlue outline and cap,
    /// an interior band filled to <paramref name="percent"/> in <paramref name="fill"/>, and the
    /// Terracotta threshold lines <paramref name="threshold"/> calls for, expressed on a 256-unit
    /// reference canvas scaled to <paramref name="size"/> with stroke floors that keep it legible at
    /// 16 px. <paramref name="heights"/> picks the proportions: <see cref="TraySlotHeights"/> for the
    /// live tray icon, <see cref="AppIconHeights"/> for everything drawn in the brand's own shape.
    /// </summary>
    /// <remarks>
    /// The fill is passed rather than sampled: the live tray style takes the gauge scale at the
    /// reading, while the canonical renders take the brand's fixed sage at a level that never moves.
    ///
    /// A deliberate hand-maintained third copy of the geometry: the two build-time generators share
    /// theirs via scripts\BatteryGlyph.ps1, but this one runs in-process and cannot shell out to
    /// PowerShell on the tray-icon path. brand\chargekeeper-icon.svg is authoritative for
    /// <see cref="AppIconHeights"/> — change it, then BatteryGlyph.ps1, then here.
    /// Tests\BrandMarkGeometryTests.cs pins the three together, and pins the two height sets apart.
    /// </remarks>
    private static Bitmap RenderMarkBitmap(int size, int percent, Color fill,
                                           ChargeThresholdState? threshold, MarkHeights heights)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode     = SmoothingMode.AntiAlias;
        g.PixelOffsetMode   = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        float s = size / MarkCanvas;

        // Battery body outline.
        var bodyRect = RectangleF.FromLTRB(15 * s, heights.BodyTop * s, 206 * s, heights.BodyBottom * s);
        using (var bodyPath = BuildRoundedRectPath(bodyRect, 6 * s))
        using (var bodyPen  = new System.Drawing.Pen(MarkSteel, Math.Max(13 * s, 1.6f))
                                   { LineJoin = LineJoin.Round })
            g.DrawPath(bodyPen, bodyPath);

        // Battery cap (positive terminal).
        using (var capPath = BuildRoundedRectPath(
                   RectangleF.FromLTRB(221 * s, heights.CapTop * s, 241 * s, heights.CapBottom * s), 3 * s))
        using (var cap     = new SolidBrush(MarkSteel))
            g.FillPath(cap, capPath);

        // Interior charge fill, at ~90 % opacity. A reading of 0 % draws an empty body rather than a
        // hairline at the left edge.
        float fillRight = MarkInteriorX(percent);
        if (fillRight > MarkInteriorLeft)
        {
            var fillRect = RectangleF.FromLTRB(MarkInteriorLeft * s, heights.InteriorTop * s,
                                               fillRight * s, heights.InteriorBottom * s);
            using var fillPath  = BuildRoundedRectPath(fillRect, 3 * s);
            using var fillBrush = new SolidBrush(Color.FromArgb(230, fill));
            g.FillPath(fillBrush, fillPath);
        }

        // The guard line IS the stop threshold, so it is drawn only while the firmware is capping.
        // Start first: where the two nearly meet, the stop mark is the one that must stay readable.
        var (stop, start) = ThresholdMarksFor(threshold);
        var contrast      = CurrentContrast();
        if (start is { } startPct) DrawMarkLine(g, size, startPct, contrast, heights, minor: true);
        if (stop  is { } stopPct)  DrawMarkLine(g, size, stopPct,  contrast, heights, minor: false);

        return bmp;
    }

    /// <summary>Draws one threshold line across the mark's interior at <paramref name="percent"/>.
    /// A halo goes down first: at 16 px the line itself is two pixels of a mid-tone, which on a
    /// light taskbar is nothing. <paramref name="minor"/> draws the thinner start mark.</summary>
    private static void DrawMarkLine(Graphics g, int size, int percent, IconContrast contrast,
                                     MarkHeights heights, bool minor)
    {
        float s      = size / MarkCanvas;
        float x      = MarkInteriorX(percent) * s;
        float top    = heights.InkTop    * s;
        float bottom = heights.InkBottom * s;
        // Flat caps and a ≥1.5 px floor, so the line survives the 16 px frame without overhanging.
        float width  = minor ? Math.Max(6 * s, 1.5f) : Math.Max(9 * s, 2f);
        Color color  = MarkTerracotta;

        using (var haloPen = new System.Drawing.Pen(contrast.Outline, width + contrast.ExtraWidth(size)))
        {
            haloPen.StartCap = haloPen.EndCap = LineCap.Flat;
            g.DrawLine(haloPen, x, top, x, bottom);
        }

        using var pen = new System.Drawing.Pen(color, width);
        pen.StartCap = pen.EndCap = LineCap.Flat;
        g.DrawLine(pen, x, top, x, bottom);
    }

    /// <summary>The mark in its canonical brand proportions, drawn natively at
    /// <paramref name="size"/> px: the same renderer as the live style but on
    /// <see cref="AppIconHeights"/>, fed the charge level and guard position that land where
    /// brand\chargekeeper-icon.svg puts them, so the pixels and the vector cannot drift. Serves the
    /// in-window chrome marks, which get their own frame size rather than a resampled one.</summary>
    internal static Bitmap RenderAppIconBitmap(int size) =>
        RenderMarkBitmap(size, MarkCanonicalPercent, MarkSage, MarkCanonicalThreshold, AppIconHeights);

    /// <summary>The mark the tray is seeded with before the first battery report, at
    /// <paramref name="size"/> px. Same canonical charge level and guard as the brand's own mark,
    /// on <see cref="TraySlotHeights"/> — the seed occupies the notification-area slot, so it takes
    /// the maximised geometry every later repaint of that slot uses.</summary>
    internal static Bitmap RenderTraySeedBitmap(int size) =>
        RenderMarkBitmap(size, MarkCanonicalPercent, MarkSage, MarkCanonicalThreshold, TraySlotHeights);

    /// <summary>The threshold state that puts the mark's guard line where the brand puts it. Start is
    /// 0 — the brand has one line, and 0 is also what a mode-based vendor reports.</summary>
    private static readonly ChargeThresholdState MarkCanonicalThreshold =
        new(Capable: true, Enabled: true, Start: 0, Stop: MarkCanonicalGuard);

    /// <summary>
    /// Renders the battery arc icon: a 100-unit virtual canvas mapped to <paramref name="size"/> px,
    /// centre 50/50, radius 33, 135° start, 270° sweep — the same proportions as the dashboard gauge.
    /// The background is transparent, so the ring has to read on any taskbar colour by itself.
    /// </summary>
    private static Bitmap RenderBatteryBitmap(int size, int percent, PowerState state,
                                              ChargeThresholdState? threshold)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode   = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        // The outer arc edge lands ~1 px inside the icon edge so antialiasing doesn't clip.
        float stroke = size * 0.19f;                    // ~6 px at 32 px
        float cx     = size / 2f;
        float cy     = size / 2f;
        float r      = cx - stroke / 2f - 1f;          // outer edge = cx + r + stroke/2 ≈ size-1

        // Track and halo are chosen from the taskbar theme: one setting cannot read on both.
        var contrast = CurrentContrast();

        using var trackPen = new System.Drawing.Pen(contrast.Track, stroke);
        trackPen.StartCap = trackPen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
        DrawArc(g, trackPen, cx, cy, r, 135f, 270f);

        if (percent > 0)
        {
            Color fillColor = FillFor(percent, state);

            // Wider dark stroke drawn first, as a halo: without it the arc has no crisp edge on a
            // light taskbar.
            using (var haloPen = new System.Drawing.Pen(contrast.Outline, stroke + contrast.ExtraWidth(size)))
            {
                haloPen.StartCap = haloPen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                DrawArc(g, haloPen, cx, cy, r, 135f, 270f * percent / 100f);
            }

            using var fillPen = new System.Drawing.Pen(fillColor, stroke);
            fillPen.StartCap = fillPen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
            DrawArc(g, fillPen, cx, cy, r, 135f, 270f * percent / 100f);
        }

        // Outside the fill branch: the marks say where the cap is, which is worth showing at a
        // reading of 0 % too. Start first, so the stop mark stays readable where the two nearly meet.
        var (stop, start) = ThresholdMarksFor(threshold);
        if (start is { } startPct) DrawArcMark(g, size, cx, cy, r, stroke, startPct, contrast, minor: true);
        if (stop  is { } stopPct)  DrawArcMark(g, size, cx, cy, r, stroke, stopPct,  contrast, minor: false);

        return bmp;
    }

    /// <summary>Draws one threshold tick radially across the ring at <paramref name="percent"/> on
    /// the arc's sweep. <paramref name="minor"/> draws the thinner start mark.</summary>
    private static void DrawArcMark(Graphics g, int size, float cx, float cy, float r, float stroke,
                                    int percent, IconContrast contrast, bool minor)
    {
        // The ring runs 270° from 135° clock-face; DrawArc turns that into GDI's 0° = 3 o'clock.
        double rad = (135f + 270f * Math.Clamp(percent, 0, 100) / 100f - 90f) * Math.PI / 180.0;
        float  dx  = (float)Math.Cos(rad);
        float  dy  = (float)Math.Sin(rad);
        float  x1  = cx + dx * (r - stroke / 2f), y1 = cy + dy * (r - stroke / 2f);
        float  x2  = cx + dx * (r + stroke / 2f), y2 = cy + dy * (r + stroke / 2f);

        float width = minor ? Math.Max(1f, size * 0.07f) : Math.Max(1.5f, size * 0.10f);
        // A narrower halo than the arc's own: this one crosses the ring rather than tracing it, and
        // the arc's extra width would swallow a third of the ring at 16 px.
        using (var haloPen = new System.Drawing.Pen(contrast.Outline, width + Math.Max(1f, size * 0.04f)))
            g.DrawLine(haloPen, x1, y1, x2, y2);

        using var pen = new System.Drawing.Pen(MarkTerracotta, width);
        g.DrawLine(pen, x1, y1, x2, y2);
    }

    /// <summary>Draws a circular arc using GDI+ (clock-face angles: 0° = 12 o'clock).</summary>
    private static void DrawArc(Graphics g, System.Drawing.Pen pen,
        float cx, float cy, float r, float startDeg, float sweepDeg)
    {
        if (sweepDeg <= 0) return;
        sweepDeg = Math.Min(sweepDeg, 359.9f);

        float left   = cx - r;
        float top    = cy - r;
        float diam   = r * 2;

        g.DrawArc(pen, left, top, diam, diam, startDeg - 90f, sweepDeg);
    }

    private static GraphicsPath BuildRoundedRectPath(Rectangle bounds, int radius)
    {
        int d    = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.X,         bounds.Y,          d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y,          d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d,   0, 90);
        path.AddArc(bounds.X,         bounds.Bottom - d, d, d,  90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Float-precision rounded rect for the scaled brand-mark geometry. The radius is
    /// clamped to half the shorter side — at 16 px a scaled radius can otherwise exceed the rect and
    /// make GDI+ arcs fold over themselves.</summary>
    private static GraphicsPath BuildRoundedRectPath(RectangleF b, float radius)
    {
        radius   = Math.Min(radius, Math.Min(b.Width, b.Height) / 2f);
        float d  = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(b.X,         b.Y,          d, d, 180, 90);
        path.AddArc(b.Right - d, b.Y,          d, d, 270, 90);
        path.AddArc(b.Right - d, b.Bottom - d, d, d,   0, 90);
        path.AddArc(b.X,         b.Bottom - d, d, d,  90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// Writes an ICO to <paramref name="stream"/> with one PNG-compressed frame per entry in
    /// <paramref name="sizes"/>, each rendered natively via <paramref name="render"/> so no size is
    /// downscaled from a larger frame. Each size must fit in a byte (0 means 256).
    /// </summary>
    private static void WriteIco(Stream stream, Func<int, Bitmap> render, int[] sizes)
    {
        var frames = Array.ConvertAll(sizes, s =>
        {
            using var bmp = render(s);
            using var ms  = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        });

        using var bw = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        // ICO file header (6 bytes)
        bw.Write((short)0);             // reserved — must be 0
        bw.Write((short)1);             // type: 1 = icon
        bw.Write((short)sizes.Length);  // number of images

        // Directory entries (16 bytes each); image data starts after header + directory.
        int dataOffset = 6 + sizes.Length * 16;
        for (int i = 0; i < sizes.Length; i++)
        {
            bw.Write((byte)sizes[i]);      // width  (0 means 256)
            bw.Write((byte)sizes[i]);      // height (0 means 256)
            bw.Write((byte)0);             // colour count (0 = true colour)
            bw.Write((byte)0);             // reserved
            bw.Write((short)1);            // colour planes
            bw.Write((short)32);           // bits per pixel
            bw.Write(frames[i].Length);    // data size in bytes
            bw.Write(dataOffset);          // data offset from start of file
            dataOffset += frames[i].Length;
        }

        foreach (var frame in frames)
            bw.Write(frame);
        bw.Flush();
    }

    /// <summary>Writes the tray seed icon to disk. Renders to a temp file and moves it into place,
    /// so a launch killed mid-render leaves no half-written .ico for the existence check to serve.</summary>
    private static void SaveAsIco(string filePath)
    {
        var tmp = filePath + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            WriteIco(fs, RenderTraySeedBitmap, IconSizes);
        File.Move(tmp, filePath, overwrite: true);
    }
}
