using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using ChargeKeeper.Services;

namespace ChargeKeeper.Helpers;

/// <summary>
/// Renders the ChargeKeeper tray icon: the static "0z0 steel battery" brand mark, written once to a
/// multi-size .ico on disk, and the live battery icon — arc, numeric or the mark itself — built in
/// memory per state change.
/// The on-disk file lets H.NotifyIcon reload the icon if it is recreated, and avoids the GDI handle
/// leak <c>Bitmap.GetHicon()</c> introduces.
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

    // Arc fill colours by charge state. System.Drawing shares no type with WinUI's Windows.UI.Color,
    // but the packed ARGB bytes in GaugePalette cross that divide.
    private static readonly Color FillGreen    = FromPacked(GaugePalette.SageGreen);   // > GreenAbovePct
    private static readonly Color FillYellow   = FromPacked(GaugePalette.Amber);       // middle tier
    private static readonly Color FillOrange   = FromPacked(GaugePalette.Terracotta);  // ≤ LowAtOrBelowPct
    private static readonly Color FillCharging = FromPacked(GaugePalette.SteelBlue);   // on AC

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

    // Shared by both tray renderers so the arc and numeric modes cannot drift on tiers.
    private static Color FillFor(int percent, bool charging) => charging
        ? FillCharging
        : percent switch
        {
            > GaugePalette.GreenAbovePct   => FillGreen,
            > GaugePalette.LowAtOrBelowPct => FillYellow,
            _                              => FillOrange,
        };

    // Stamped into the filename so an in-place app update regenerates the icon rather than serving
    // the previous version's cached file. Bump on any change to the mark.
    private const string IconVersion = "v8";

    /// <summary>Generates the multi-size ICO file and returns its path, or returns the cached path
    /// when it already exists.</summary>
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
    /// </summary>
    internal static System.Drawing.Icon RenderBatteryIcon(
        int percent, bool charging, TrayIconMode mode = TrayIconMode.Arc)
    {
        Bitmap Render(int size) => RenderStyleBitmap(size, percent, charging, mode);

        using var ms = new MemoryStream();
        WriteIco(ms, Render, [CurrentTraySlotSize()]);
        ms.Position = 0;
        return new System.Drawing.Icon(ms);
    }

    /// <summary>One frame of the selected style at <paramref name="size"/> px. Split out from
    /// <see cref="RenderBatteryIcon"/> so a caller can render a known size rather than whatever the
    /// live tray slot happens to be.</summary>
    internal static Bitmap RenderStyleBitmap(int size, int percent, bool charging, TrayIconMode mode) =>
        mode switch
        {
            TrayIconMode.Numeric   => RenderNumericBitmap(size, percent, charging),
            TrayIconMode.BrandMark => RenderMarkBitmap(size, percent, charging),
            _                      => RenderBatteryBitmap(size, percent, charging),
        };

    /// <summary>Renders the percentage as a large number on a colour-coded rounded square.</summary>
    private static Bitmap RenderNumericBitmap(int size, int percent, bool charging)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode   = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        Color bg = FillFor(percent, charging);

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
    // the same scale.
    //
    // The vertical figures are the SVG's scaled 1.6x about the centre line: the mark's proportions
    // are landscape and the tray slot is square, so it used to be a letterbox with 48 % of the frame
    // height in use. The horizontal figures are untouched — they already spanned 91 %.
    private const float MarkInteriorLeft  = 36f;
    private const float MarkInteriorRight = 185f;
    private const float MarkInteriorTop    = 72f;
    private const float MarkInteriorBottom = 185f;

    // Body, cap and the guard line's vertical extent. The guard overhangs the body top and bottom,
    // so its extent is the mark's full ink height.
    private const float MarkBodyTop    = 51f;
    private const float MarkBodyBottom = 205f;
    private const float MarkCapTop     = 93f;
    private const float MarkCapBottom  = 163f;
    internal const float MarkInkTop    = 29f;
    internal const float MarkInkBottom = 227f;

    /// <summary>The reference canvas the mark's figures are expressed on.</summary>
    internal const float MarkCanvas = 256f;

    // The charge level and guard position that reproduce brand\chargekeeper-icon.svg's fixed fill
    // rect and guard line. 76 % rather than the exact 74 % the rect measures, because 74 lands in
    // the amber tier and the mark's interior is sage.
    internal const int MarkCanonicalPercent = 76;
    internal const int MarkCanonicalGuard   = 84;

    /// <summary>The x on the reference canvas where <paramref name="percent"/> falls in the mark's
    /// interior band.</summary>
    internal static float MarkInteriorX(int percent) =>
        MarkInteriorLeft + (MarkInteriorRight - MarkInteriorLeft) * Math.Clamp(percent, 0, 100) / 100f;

    /// <summary>
    /// Renders the "0z0 steel battery" mark on a transparent background: a SteelBlue outline and cap,
    /// an interior fill in the charge tier's colour at <paramref name="percent"/>, and a Terracotta
    /// guard line, expressed on a 256-unit reference canvas scaled to <paramref name="size"/> with
    /// stroke floors that keep it legible at 16 px.
    /// </summary>
    /// <remarks>
    /// A deliberate hand-maintained third copy of the geometry: the two build-time generators share
    /// theirs via scripts\BatteryGlyph.ps1, but this one runs in-process and cannot shell out to
    /// PowerShell on the tray-icon path. brand\chargekeeper-icon.svg is authoritative — change it,
    /// then BatteryGlyph.ps1, then here. No test catches the three drifting apart.
    /// </remarks>
    private static Bitmap RenderMarkBitmap(int size, int percent, bool charging)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode     = SmoothingMode.AntiAlias;
        g.PixelOffsetMode   = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        float s = size / MarkCanvas;

        // Battery body outline.
        var bodyRect = RectangleF.FromLTRB(15 * s, MarkBodyTop * s, 206 * s, MarkBodyBottom * s);
        using (var bodyPath = BuildRoundedRectPath(bodyRect, 6 * s))
        using (var bodyPen  = new System.Drawing.Pen(MarkSteel, Math.Max(13 * s, 1.6f))
                                   { LineJoin = LineJoin.Round })
            g.DrawPath(bodyPen, bodyPath);

        // Battery cap (positive terminal).
        using (var capPath = BuildRoundedRectPath(
                   RectangleF.FromLTRB(221 * s, MarkCapTop * s, 241 * s, MarkCapBottom * s), 3 * s))
        using (var cap     = new SolidBrush(MarkSteel))
            g.FillPath(cap, capPath);

        // Interior charge fill, at ~90 % opacity. A reading of 0 % draws an empty body rather than a
        // hairline at the left edge.
        float fillRight = MarkInteriorX(percent);
        if (fillRight > MarkInteriorLeft)
        {
            var fillRect = RectangleF.FromLTRB(MarkInteriorLeft * s, MarkInteriorTop * s,
                                               fillRight * s, MarkInteriorBottom * s);
            using var fillPath  = BuildRoundedRectPath(fillRect, 3 * s);
            using var fillBrush = new SolidBrush(Color.FromArgb(230, FillFor(percent, charging)));
            g.FillPath(fillBrush, fillPath);
        }

        DrawMarkLine(g, size, MarkCanonicalGuard, MarkTerracotta, CurrentContrast());

        return bmp;
    }

    /// <summary>Draws one vertical line across the mark's interior at <paramref name="percent"/> — the
    /// guard line, and the threshold marks that sit on the same scale. A halo goes down first: at
    /// 16 px the line itself is two pixels of a mid-tone, which on a light taskbar is nothing.</summary>
    private static void DrawMarkLine(Graphics g, int size, int percent, Color color, IconContrast contrast)
    {
        float s      = size / MarkCanvas;
        float x      = MarkInteriorX(percent) * s;
        float top    = MarkInkTop    * s;
        float bottom = MarkInkBottom * s;
        // Flat caps and a ≥2 px floor, so the line survives the 16 px frame without overhanging.
        float width  = Math.Max(9 * s, 2f);

        using (var haloPen = new System.Drawing.Pen(contrast.Outline, width + contrast.ExtraWidth(size)))
        {
            haloPen.StartCap = haloPen.EndCap = LineCap.Flat;
            g.DrawLine(haloPen, x, top, x, bottom);
        }

        using var pen = new System.Drawing.Pen(color, width);
        pen.StartCap = pen.EndCap = LineCap.Flat;
        g.DrawLine(pen, x, top, x, bottom);
    }

    /// <summary>The mark in its canonical brand proportions, for the static on-disk .ico: the same
    /// renderer as the live style, fed the charge level and guard position that land where
    /// brand\chargekeeper-icon.svg puts them, so the file and the style cannot drift.</summary>
    private static Bitmap RenderIconBitmap(int size) =>
        RenderMarkBitmap(size, MarkCanonicalPercent, charging: false);

    /// <summary>
    /// Renders the battery arc icon: a 100-unit virtual canvas mapped to <paramref name="size"/> px,
    /// centre 50/50, radius 33, 135° start, 270° sweep — the same proportions as the dashboard gauge.
    /// The background is transparent, so the ring has to read on any taskbar colour by itself.
    /// </summary>
    private static Bitmap RenderBatteryBitmap(int size, int percent, bool charging)
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
            Color fillColor = FillFor(percent, charging);

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

        return bmp;
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

    /// <summary>Writes the static brand icon to disk. Renders to a temp file and moves it into place,
    /// so a launch killed mid-render leaves no half-written .ico for the existence check to serve.</summary>
    private static void SaveAsIco(string filePath)
    {
        var tmp = filePath + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            WriteIco(fs, RenderIconBitmap, IconSizes);
        File.Move(tmp, filePath, overwrite: true);
    }
}
