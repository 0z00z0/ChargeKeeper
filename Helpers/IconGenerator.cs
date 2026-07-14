using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using ChargeKeeper.Services;

namespace ChargeKeeper.Helpers;

/// <summary>
/// Generates the ChargeKeeper "Guarded Battery" tray icon at runtime — the static fallback
/// shown at startup until the first battery event replaces it with the live arc/number icon.
/// Writing to a file on disk lets H.NotifyIcon reload the icon if it is recreated,
/// and avoids the GDI handle leak that <c>Bitmap.GetHicon()</c> introduces.
/// </summary>
internal static class IconGenerator
{
    // Rounded-square background geometry shared by the numeric icon renderer.
    private const float CornerRadiusFraction = 0.18f; // rounded-square corner radius
    private const float MarginFraction        = 0.04f; // gap from icon edge to background square

    // Sizes baked into the STATIC on-disk .ico — covers 100/125/150/200% tray DPI without upscaling.
    // The LIVE icon renders only the current tray slot size instead (see RenderBatteryIcon).
    private static readonly int[] IconSizes = [32, 24, 20, 16];

    // SM_CXSMICON — the shell's small-icon (notification-area) width in pixels for the current DPI.
    private const int SM_CXSMICON = 49;

    // Classic DllImport (not the LibraryImport source generator) so this stays local to
    // IconGenerator without forcing <AllowUnsafeBlocks> on the whole project. Kept here rather than
    // in NativeMethods because it's used only by the tray-icon renderer.
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    /// <summary>
    /// The tray slot size the shell will actually display, snapped to a sane range. Used to render
    /// the LIVE icon at exactly one size rather than the four the static .ico bakes in — the shell
    /// only ever shows one, so rendering the other three every state change was wasted GDI work.
    /// Falls back to 16 (100% DPI small icon) if the metric is unavailable.
    /// </summary>
    private static int CurrentTraySlotSize()
    {
        int size;
        try { size = GetSystemMetrics(SM_CXSMICON); }
        catch { size = 0; }
        // Clamp defensively: a bogus 0 (metric unavailable) or an absurd value must not yield a
        // giant/empty bitmap. 16..64 covers 100%..400% small-icon DPI.
        return size is >= 16 and <= 64 ? size : 16;
    }

    // ── ChargeKeeper "Guarded Battery" brand palette ──────────────────────────
    // Same design as Assets\AppIcon.ico / brand\chargekeeper-icon.svg (the authoritative
    // vector), redrawn natively per frame from a 256-unit reference canvas.
    private static readonly Color BodyLight  = Color.FromArgb(0x7B, 0x8C, 0xFF); // purple (gradient start)
    private static readonly Color BodyDark   = Color.FromArgb(0x3F, 0x5B, 0xE0); // indigo (gradient end)
    private static readonly Color LimitAmber = FromPacked(GaugePalette.Amber);   // charge-limit line (== brand amber)

    // ── Arc fill colours — charge state (TODO #34) ────────────────────────────
    // System.Drawing has no shared type with WinUI's Windows.UI.Color, but packed ARGB bytes cross
    // that divide fine: these build from the same GaugePalette constants AppColors uses, so the
    // tray icon and the dashboard gauge are consistent (TODO #34) structurally instead of by a
    // "keep in sync BY HAND" comment.
    private static readonly Color FillGreen    = FromPacked(GaugePalette.SageGreen);   // > GreenAbovePct
    private static readonly Color FillYellow   = FromPacked(GaugePalette.Amber);       // middle tier
    private static readonly Color FillOrange   = FromPacked(GaugePalette.Terracotta);  // ≤ LowAtOrBelowPct
    private static readonly Color FillCharging = FromPacked(GaugePalette.SteelBlue);   // on AC

    private static Color FromPacked(uint argb) => Color.FromArgb(unchecked((int)argb));

    // Charge-state fill colour shared by BOTH tray renderers (arc + numeric) and matching the
    // dashboard gauge via GaugePalette — one switch so the two icon modes can't drift on tiers.
    private static Color FillFor(int percent, bool charging) => charging
        ? FillCharging
        : percent switch
        {
            > GaugePalette.GreenAbovePct   => FillGreen,
            > GaugePalette.LowAtOrBelowPct => FillYellow,
            _                              => FillOrange,
        };

    /// <summary>
    /// Generates a multi-size ICO file and returns its path.
    /// The file is created once; subsequent calls return the cached path immediately.
    /// </summary>
    // Version stamp baked into the filename so an in-place app update regenerates the icon
    // automatically rather than serving the stale cached file from a previous version.
    // v5: the red Lenovo "L" was replaced by the ChargeKeeper "Guarded Battery" mark.
    // v6: dropped the dark background plate (transparent, scaled to fill the canvas).
    private const string IconVersion = "v6";

    internal static string GenerateAndSaveTrayIcon(string outputDirectory)
    {
        var icoPath = Path.Combine(outputDirectory, $"ChargeKeeper-{IconVersion}.ico");
        if (File.Exists(icoPath)) return icoPath;

        SaveAsIco(icoPath);
        return icoPath;
    }

    /// <summary>
    /// Renders a live battery-level tray icon as a single-frame <see cref="System.Drawing.Icon"/>
    /// natively at the current tray-slot size (see <see cref="WriteIco"/> / <see cref="CurrentTraySlotSize"/>).
    /// When <paramref name="mode"/> is <see cref="TrayIconMode.Numeric"/> the icon shows a
    /// large percentage number on a colour-coded background instead of an arc gauge.
    /// The returned icon owns an independent, data-backed handle, so the caller can safely
    /// <see cref="System.Drawing.Icon.Dispose"/> it once a newer icon replaces it.
    /// </summary>
    /// <remarks>
    /// Renders at exactly the current tray slot size (<see cref="CurrentTraySlotSize"/>) rather than
    /// baking all four <see cref="IconSizes"/> the way the static on-disk icon does: the shell only
    /// ever displays one slot, so the other three frames were rendered and PNG-encoded on every
    /// state change for nothing. Rendering natively at the real slot size (instead of an earlier
    /// approach that rendered one 32px frame and let the shell downscale it — which washed out the
    /// thin ~6px semi-transparent arc stroke, especially the low-battery Terracotta) keeps the
    /// colour crisp while doing a quarter of the work.
    /// </remarks>
    internal static System.Drawing.Icon RenderBatteryIcon(
        int percent, bool charging, TrayIconMode mode = TrayIconMode.Arc)
    {
        Bitmap Render(int size) => mode == TrayIconMode.Numeric
            ? RenderNumericBitmap(size, percent, charging)
            : RenderBatteryBitmap(size, percent, charging);

        using var ms = new MemoryStream();
        WriteIco(ms, Render, [CurrentTraySlotSize()]);
        ms.Position = 0;
        return new System.Drawing.Icon(ms);  // owns its data + handle; safe to dispose
    }

    /// <summary>
    /// Renders a 32×32 tray icon that displays the percentage as a large number on a
    /// colour-coded rounded-square background (same colour scheme as the arc icon).
    /// </summary>
    private static Bitmap RenderNumericBitmap(int size, int percent, bool charging)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode   = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        // Colour-coded background — same palette, thresholds, and charging override as the arc
        // fill (TODO #34). This mode previously used its own hardcoded vivid traffic-light colours
        // (a teal-green/orange/red scheme at 50/20% cutoffs) left over from before #34 introduced
        // the shared palette, so a discharging battery at low charge showed a jarring saturated
        // red/orange here instead of the muted Terracotta everywhere else in the app — reusing the
        // same Fill* constants and GaugePalette cutoffs as RenderBatteryBitmap fixes that.
        Color bg = FillFor(percent, charging);

        int margin = Math.Max(1, (int)Math.Round(size * MarginFraction));
        var rect   = new Rectangle(margin, margin, size - margin * 2 - 1, size - margin * 2 - 1);
        int radius = Math.Max(2, (int)Math.Round(size * CornerRadiusFraction));
        using (var bgBrush = new SolidBrush(bg))
        using (var path    = BuildRoundedRectPath(rect, radius))
            g.FillPath(bgBrush, path);

        // Large % number filling the icon — sized to stay legible after Windows downscales
        // the 32 px bitmap to the ~16 px tray slot. Three-digit "100" is scaled down to fit.
        string label  = percent > 0 ? $"{percent}" : "?";
        float  emSize = size * (label.Length >= 3 ? 0.46f : 0.66f);
        using var sf  = new StringFormat
        {
            Alignment     = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags   = StringFormatFlags.NoWrap,
            Trimming      = StringTrimming.None,
        };

        // Draw the digits as a path with a dark outline + white fill. Plain white is invisible
        // on the light-green "healthy/charging" background; the dark halo keeps the number
        // readable on every background colour (green / orange / red).
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

    // ── Private rendering ─────────────────────────────────────────────────────

    /// <summary>
    /// Renders the "Guarded Battery" mark: a battery outline in a purple→indigo gradient, with
    /// an amber vertical line at the ~80 % mark representing the charge-limit threshold. Fully
    /// transparent background — no background plate — geometry scaled to fill the canvas.
    /// Geometry is expressed on a 256-unit reference canvas (the same numbers as
    /// <c>brand\chargekeeper-icon.svg</c>) and scaled to <paramref name="size"/>, with minimum
    /// stroke widths so the mark stays legible at 16 px.
    /// </summary>
    private static Bitmap RenderIconBitmap(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode     = SmoothingMode.AntiAlias;
        g.PixelOffsetMode   = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        float s = size / 256f;

        // Battery body outline: purple→indigo gradient stroke.
        var bodyRect = new RectangleF(13 * s, 78 * s, 195 * s, 100 * s);
        using (var bodyPath  = BuildRoundedRectPath(bodyRect, 23 * s))
        using (var bodyBrush = new LinearGradientBrush(bodyRect, BodyLight, BodyDark,
                                                       LinearGradientMode.ForwardDiagonal))
        using (var bodyPen   = new System.Drawing.Pen(bodyBrush, Math.Max(15 * s, 1.6f))
                                   { LineJoin = LineJoin.Round })
            g.DrawPath(bodyPen, bodyPath);

        // Battery cap (positive terminal): solid indigo.
        using (var capPath = BuildRoundedRectPath(new RectangleF(221 * s, 103 * s, 23 * s, 50 * s), 9 * s))
        using (var cap     = new SolidBrush(BodyDark))
            g.FillPath(cap, capPath);

        // Interior charge fill: same gradient at ~85 % opacity, filled to ~80 % of the body.
        var fillRect = new RectangleF(36 * s, 101 * s, 110 * s, 55 * s);
        using (var fillPath  = BuildRoundedRectPath(fillRect, 11 * s))
        using (var fillBrush = new LinearGradientBrush(fillRect,
                   Color.FromArgb(217, BodyLight), Color.FromArgb(217, BodyDark),
                   LinearGradientMode.ForwardDiagonal))
            g.FillPath(fillBrush, fillPath);

        // Amber charge-limit line at the 80 % mark, slightly overshooting the body top/bottom.
        // Clamped to ≥2 px so it survives the 16 px frame.
        using (var limitPen = new System.Drawing.Pen(LimitAmber, Math.Max(9 * s, 2f)))
        {
            limitPen.StartCap = limitPen.EndCap = LineCap.Round;
            g.DrawLine(limitPen, 161 * s, 63 * s, 161 * s, 193 * s);
        }

        return bmp;
    }

    /// <summary>
    /// Renders a 32×32 battery arc icon.
    /// Arc geometry: 100×100 virtual canvas mapped to <paramref name="size"/> px,
    /// centre 50/50, radius 33, 7-o'clock start (135°), 270° sweep — same proportions
    /// as the DashboardWindow gauge so the two visuals feel consistent.
    /// Fully transparent background (no plate) — the ring itself must read on any taskbar
    /// colour, so the track uses a translucent mid-grey (readable against both light and
    /// dark backgrounds via alpha blending) instead of the old dark-plate-relative dim grey,
    /// and the coloured fill arc gets a thin dark halo stroke underneath it so it doesn't look
    /// "floaty" without a backdrop on light taskbars specifically.
    /// </summary>
    private static Bitmap RenderBatteryBitmap(int size, int percent, bool charging)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode   = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        // Arc geometry: stroke fills as much of the icon as possible.
        // Outer arc edge lands ~1 px inside the icon edge so antialiasing doesn't clip.
        float stroke = size * 0.19f;                    // ~6 px at 32 px
        float cx     = size / 2f;
        float cy     = size / 2f;
        float r      = cx - stroke / 2f - 1f;          // outer edge = cx + r + stroke/2 ≈ size-1

        // Track (empty portion of ring) — translucent mid-grey blends readably against both a
        // dark and a light taskbar now that there's no dark plate behind it to contrast against.
        using var trackPen = new System.Drawing.Pen(Color.FromArgb(160, 140, 140, 140), stroke);
        trackPen.StartCap = trackPen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
        DrawArc(g, trackPen, cx, cy, r, 135f, 270f);

        if (percent > 0)
        {
            // Fill colour by charge state (TODO #34): green above GreenAbovePct, orange at/below
            // LowAtOrBelowPct, amber between, charging/on-AC forces blue. Values are the muted
            // shared palette (GaugePalette → same bytes AppColors builds from), not a vivid
            // traffic-light scheme — the gauge and this icon are consistent structurally.
            Color fillColor = FillFor(percent, charging);

            // Thin dark halo just behind the coloured fill (slightly wider stroke, drawn first)
            // so the arc keeps a crisp edge on a light/white taskbar now that there's no dark
            // plate providing that contrast for free.
            using (var haloPen = new System.Drawing.Pen(Color.FromArgb(90, 0, 0, 0), stroke + Math.Max(1.5f, size * 0.06f)))
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

        // GDI+ angles: 0° = 3 o'clock, increases clockwise.
        // Clock-face: 0° = 12 o'clock → subtract 90°.
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

    /// <summary>
    /// Float-precision rounded rect used by the scaled brand-mark geometry. The radius is
    /// clamped to half the shorter side — at 16 px some scaled radii would otherwise exceed
    /// the rect and make GDI+ arcs fold over themselves.
    /// </summary>
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
    /// Writes a valid ICO to <paramref name="stream"/> with one PNG-compressed frame per entry in
    /// <paramref name="sizes"/>, each rendered natively via <paramref name="render"/> (no
    /// downscaling from a single larger frame) so every size stays sharp. PNG-in-ICO is supported by
    /// Windows Vista and later. Shared by <see cref="SaveAsIco"/> (the static brand icon → all four
    /// <see cref="IconSizes"/>, written to a file) and <see cref="RenderBatteryIcon"/> (the live
    /// arc/numeric icon → the single current tray-slot size, built in memory on every state change).
    /// Each size must fit in a byte (0 means 256); the callers only ever pass 16..64.
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

        // Image data
        foreach (var frame in frames)
            bw.Write(frame);
        bw.Flush();
    }

    /// <summary>Writes the static brand icon to disk as a multi-resolution ICO file.</summary>
    private static void SaveAsIco(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        WriteIco(fs, RenderIconBitmap, IconSizes);
    }
}
