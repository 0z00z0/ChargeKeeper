using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
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

    // Sizes baked into the .ico — covers 100/125/150/200% tray DPI without upscaling.
    private static readonly int[] IconSizes = [32, 24, 20, 16];

    // ── ChargeKeeper "Guarded Battery" brand palette ──────────────────────────
    // Same design as Assets\AppIcon.ico / brand\chargekeeper-icon.svg (the authoritative
    // vector), redrawn natively per frame from a 256-unit reference canvas.
    private static readonly Color BodyLight  = Color.FromArgb(0x7B, 0x8C, 0xFF); // purple (gradient start)
    private static readonly Color BodyDark   = Color.FromArgb(0x3F, 0x5B, 0xE0); // indigo (gradient end)
    private static readonly Color LimitAmber = Color.FromArgb(0xD8, 0xA6, 0x57); // charge-limit line

    // ── Arc fill colours — charge state (TODO #34) ────────────────────────────
    // System.Drawing has no shared type with WinUI's Windows.UI.Color, so these are literal
    // duplicates of Helpers\AppColors.cs's SageGreen/Amber/Terracotta/SteelBlue — same ARGB bytes,
    // kept in sync BY HAND. This is the actual mechanism that makes the tray icon and the
    // dashboard gauge "consistent" (TODO #34): if AppColors' values change, these must change
    // with them.
    private static readonly Color FillGreen    = Color.FromArgb(0x7A, 0xB8, 0x8F); // == AppColors.SageGreen  (> 75 %)
    private static readonly Color FillYellow   = Color.FromArgb(0xD8, 0xA6, 0x57); // == AppColors.Amber      (26-75 %)
    private static readonly Color FillOrange   = Color.FromArgb(0xC9, 0x92, 0x6B); // == AppColors.Terracotta (<= 25 %)
    private static readonly Color FillCharging = Color.FromArgb(0x7F, 0xA8, 0xB8); // == AppColors.SteelBlue  (on AC)

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
    /// Renders a live battery-level tray icon as a multi-resolution <see cref="System.Drawing.Icon"/>
    /// (one native frame per <see cref="IconSizes"/> entry — see <see cref="WriteMultiSizeIco"/>).
    /// When <paramref name="mode"/> is <see cref="TrayIconMode.Numeric"/> the icon shows a
    /// large percentage number on a colour-coded background instead of an arc gauge.
    /// The returned icon owns an independent, data-backed handle, so the caller can safely
    /// <see cref="System.Drawing.Icon.Dispose"/> it once a newer icon replaces it.
    /// </summary>
    /// <remarks>
    /// Previously rendered a single 32×32 frame and let the shell downscale it to the actual
    /// tray slot size (commonly 16px, sometimes 20/24 at higher DPI). A thin (~6px at 32px)
    /// semi-transparent stroke loses most of its contrast under that generic bitmap scaling,
    /// especially the low-battery tier's already-muted Terracotta sitting right next to the
    /// translucent grey track/halo — the net effect reads as a washed-out grey ring rather than
    /// a recognisable colour. Rendering each size natively (same approach <see cref="SaveAsIco"/>
    /// already used for the static brand icon) avoids that entirely.
    /// </remarks>
    internal static System.Drawing.Icon RenderBatteryIcon(
        int percent, bool charging, TrayIconMode mode = TrayIconMode.Arc)
    {
        Bitmap Render(int size) => mode == TrayIconMode.Numeric
            ? RenderNumericBitmap(size, percent, charging)
            : RenderBatteryBitmap(size, percent, charging);

        using var ms = new MemoryStream();
        WriteMultiSizeIco(ms, Render);
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
        // the shared AppColors palette, so a discharging battery at low charge showed a jarring
        // saturated red/orange here instead of the muted Terracotta everywhere else in the app —
        // reusing the same Fill* constants and 75/25 cutoffs as RenderBatteryBitmap fixes that.
        Color bg = percent switch
        {
            > 75 => FillGreen,
            > 25 => FillYellow,
            _    => FillOrange,
        };
        if (charging) bg = FillCharging;

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
            // Fill colour by charge state (TODO #34): green > 75 %, yellow 26-75 %, orange
            // <= 25 %, charging/on-AC forces blue. Values are the muted AppColors palette, not
            // a vivid traffic-light scheme — see the Fill* constants above for why these exact
            // hex literals (not new ones) were chosen: they match AppColors.cs byte-for-byte so
            // the gauge and this icon are actually consistent, not just visually similar.
            Color fillColor = percent switch
            {
                > 75 => FillGreen,
                > 25 => FillYellow,
                _    => FillOrange,
            };
            if (charging) fillColor = FillCharging;

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
    /// Writes a valid ICO to <paramref name="stream"/> with one PNG-compressed frame per size in
    /// <see cref="IconSizes"/>, each rendered natively via <paramref name="render"/> (no
    /// downscaling from a single larger frame) so small sizes stay sharp — the shell picks
    /// whichever entry best matches the actual tray/DPI slot instead of scaling one frame itself.
    /// PNG-in-ICO is supported by Windows Vista and later. Shared by <see cref="SaveAsIco"/> (the
    /// static brand icon, written to a file) and <see cref="RenderBatteryIcon"/> (the live
    /// arc/numeric icon, built in memory on every state change).
    /// </summary>
    private static void WriteMultiSizeIco(Stream stream, Func<int, Bitmap> render)
    {
        var frames = Array.ConvertAll(IconSizes, s =>
        {
            using var bmp = render(s);
            using var ms  = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        });

        using var bw = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        // ICO file header (6 bytes)
        bw.Write((short)0);                // reserved — must be 0
        bw.Write((short)1);                // type: 1 = icon
        bw.Write((short)IconSizes.Length); // number of images

        // Directory entries (16 bytes each); image data starts after header + directory.
        int dataOffset = 6 + IconSizes.Length * 16;
        for (int i = 0; i < IconSizes.Length; i++)
        {
            bw.Write((byte)IconSizes[i]);  // width  (0 means 256)
            bw.Write((byte)IconSizes[i]);  // height (0 means 256)
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
        WriteMultiSizeIco(fs, RenderIconBitmap);
    }
}
