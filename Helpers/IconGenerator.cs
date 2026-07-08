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
    private static readonly Color BgCenter   = Color.FromArgb(0x15, 0x26, 0x3A); // radial centre-top
    private static readonly Color BgEdge     = Color.FromArgb(0x0A, 0x0F, 0x17); // radial edge
    private static readonly Color BodyLight  = Color.FromArgb(0x7B, 0x8C, 0xFF); // purple (gradient start)
    private static readonly Color BodyDark   = Color.FromArgb(0x3F, 0x5B, 0xE0); // indigo (gradient end)
    private static readonly Color LimitAmber = Color.FromArgb(0xD8, 0xA6, 0x57); // charge-limit line

    /// <summary>
    /// Generates a multi-size ICO file and returns its path.
    /// The file is created once; subsequent calls return the cached path immediately.
    /// </summary>
    // Version stamp baked into the filename so an in-place app update regenerates the icon
    // automatically rather than serving the stale cached file from a previous version.
    // v5: the red Lenovo "L" was replaced by the ChargeKeeper "Guarded Battery" mark.
    private const string IconVersion = "v5";

    internal static string GenerateAndSaveTrayIcon(string outputDirectory)
    {
        var icoPath = Path.Combine(outputDirectory, $"ChargeKeeper-{IconVersion}.ico");
        if (File.Exists(icoPath)) return icoPath;

        SaveAsIco(icoPath);
        return icoPath;
    }

    /// <summary>
    /// Renders a live battery-level tray icon as a 32×32 <see cref="System.Drawing.Icon"/>.
    /// When <paramref name="mode"/> is <see cref="TrayIconMode.Numeric"/> the icon shows a
    /// large percentage number on a colour-coded background instead of an arc gauge.
    /// The returned icon owns an independent, data-backed handle, so the caller can safely
    /// <see cref="System.Drawing.Icon.Dispose"/> it once a newer icon replaces it.
    /// </summary>
    /// <remarks>
    /// Do NOT use the tempting <c>Icon.FromHandle(bmp.GetHicon()).Clone()</c> pattern here.
    /// An icon created from a bare HICON carries no icon data, so <c>Clone()</c> merely copies
    /// the handle <em>reference</em> rather than the bitmap. Destroying the source HICON then
    /// leaves the returned icon with a dangling handle; the shell faults when it paints it
    /// (Shell_NotifyIcon → access violation in CoreMessagingXP, 0xc000027b) and a later
    /// Dispose double-frees it. Instead, serialise the rendered bitmap to an in-memory .ico
    /// stream and reload it: the resulting icon owns its own data and handle — the same
    /// guarantee a file-loaded icon gives, without per-tick disk I/O.
    /// </remarks>
    internal static System.Drawing.Icon RenderBatteryIcon(
        int percent, bool charging, TrayIconMode mode = TrayIconMode.Arc)
    {
        using var bmp = mode == TrayIconMode.Numeric
                            ? RenderNumericBitmap(32, percent, charging)
                            : RenderBatteryBitmap(32, percent, charging);

        // GetHicon() returns a GDI handle we must free; wrap it only long enough to serialise
        // the icon bits into a stream, then destroy the handle. The reloaded icon is fully
        // independent of this handle.
        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using var tmp = System.Drawing.Icon.FromHandle(hIcon);
            using var ms  = new MemoryStream();
            tmp.Save(ms);            // writes the icon in .ico format (populates icon data)
            ms.Position = 0;
            return new System.Drawing.Icon(ms);  // owns its data + handle; safe to dispose
        }
        finally
        {
            NativeMethods.DestroyIcon(hIcon);
        }
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

        // Colour-coded background (same scheme as the arc fill).
        Color bg = percent switch
        {
            > 50 => Color.FromArgb(255, 0x22, 0xD3, 0x9A),
            > 20 => Color.FromArgb(255, 0xFF, 0xA5, 0x20),
            _    => Color.FromArgb(255, 0xEF, 0x44, 0x44),
        };
        if (charging) bg = Color.FromArgb(255, 0x22, 0xD3, 0x9A);

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
    /// Renders the "Guarded Battery" mark: a battery outline in a purple→indigo gradient on a
    /// dark rounded square, with an amber vertical line at the ~80 % mark representing the
    /// charge-limit threshold. Geometry is expressed on a 256-unit reference canvas (the same
    /// numbers as <c>brand\chargekeeper-icon.svg</c>) and scaled to <paramref name="size"/>,
    /// with minimum stroke widths so the mark stays legible at 16 px.
    /// </summary>
    private static Bitmap RenderIconBitmap(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode     = SmoothingMode.AntiAlias;
        g.PixelOffsetMode   = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        float s = size / 256f;

        // Dark rounded-square background with a subtle centre-top radial gradient.
        using (var bgPath = BuildRoundedRectPath(new RectangleF(8 * s, 8 * s, 240 * s, 240 * s), 52 * s))
        using (var bg     = new PathGradientBrush(bgPath))
        {
            bg.CenterColor    = BgCenter;
            bg.CenterPoint    = new PointF(size / 2f, size * 0.28f);
            bg.SurroundColors = [BgEdge];
            g.FillPath(bg, bgPath);
        }

        // Battery body outline: purple→indigo gradient stroke.
        var bodyRect = new RectangleF(40 * s, 92 * s, 156 * s, 80 * s);
        using (var bodyPath  = BuildRoundedRectPath(bodyRect, 18 * s))
        using (var bodyBrush = new LinearGradientBrush(bodyRect, BodyLight, BodyDark,
                                                       LinearGradientMode.ForwardDiagonal))
        using (var bodyPen   = new System.Drawing.Pen(bodyBrush, Math.Max(12 * s, 1.6f))
                                   { LineJoin = LineJoin.Round })
            g.DrawPath(bodyPen, bodyPath);

        // Battery cap (positive terminal): solid indigo.
        using (var capPath = BuildRoundedRectPath(new RectangleF(206 * s, 112 * s, 18 * s, 40 * s), 7 * s))
        using (var cap     = new SolidBrush(BodyDark))
            g.FillPath(cap, capPath);

        // Interior charge fill: same gradient at ~85 % opacity, filled to ~80 % of the body.
        var fillRect = new RectangleF(58 * s, 110 * s, 88 * s, 44 * s);
        using (var fillPath  = BuildRoundedRectPath(fillRect, 9 * s))
        using (var fillBrush = new LinearGradientBrush(fillRect,
                   Color.FromArgb(217, BodyLight), Color.FromArgb(217, BodyDark),
                   LinearGradientMode.ForwardDiagonal))
            g.FillPath(fillBrush, fillPath);

        // Amber charge-limit line at the 80 % mark, slightly overshooting the body top/bottom.
        // Clamped to ≥2 px so it survives the 16 px frame.
        using (var limitPen = new System.Drawing.Pen(LimitAmber, Math.Max(7 * s, 2f)))
        {
            limitPen.StartCap = limitPen.EndCap = LineCap.Round;
            g.DrawLine(limitPen, 158 * s, 80 * s, 158 * s, 184 * s);
        }

        return bmp;
    }

    /// <summary>
    /// Renders a 32×32 battery arc icon.
    /// Arc geometry: 100×100 virtual canvas mapped to <paramref name="size"/> px,
    /// centre 50/50, radius 33, 7-o'clock start (135°), 270° sweep — same proportions
    /// as the DashboardWindow gauge so the two visuals feel consistent.
    /// A dark rounded-square background ensures the arc is readable on any taskbar colour.
    /// </summary>
    private static Bitmap RenderBatteryBitmap(int size, int percent, bool charging)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode   = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        // Dark rounded-square background filling the full icon area.
        var rect   = new Rectangle(0, 0, size, size);
        int radius = Math.Max(3, (int)Math.Round(size * CornerRadiusFraction));
        using (var bgBrush = new SolidBrush(Color.FromArgb(255, 22, 22, 22)))
        using (var path    = BuildRoundedRectPath(rect, radius))
            g.FillPath(bgBrush, path);

        // Arc geometry: stroke fills as much of the icon as possible.
        // Outer arc edge lands ~1 px inside the icon edge so antialiasing doesn't clip.
        float stroke = size * 0.19f;                    // ~6 px at 32 px
        float cx     = size / 2f;
        float cy     = size / 2f;
        float r      = cx - stroke / 2f - 1f;          // outer edge = cx + r + stroke/2 ≈ size-1

        // Track (empty portion of ring) — dimly visible on the dark background.
        using var trackPen = new System.Drawing.Pen(Color.FromArgb(255, 60, 60, 60), stroke);
        trackPen.StartCap = trackPen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
        DrawArc(g, trackPen, cx, cy, r, 135f, 270f);

        if (percent > 0)
        {
            // Fill colour: green > 50 %, orange 21–50 %, red ≤ 20 %.
            Color fillColor = percent switch
            {
                > 50 => Color.FromArgb(255, 0x22, 0xD3, 0x9A),  // green
                > 20 => Color.FromArgb(255, 0xFF, 0xA5, 0x20),  // orange
                _    => Color.FromArgb(255, 0xEF, 0x44, 0x44),  // red
            };
            if (charging) fillColor = Color.FromArgb(255, 0x22, 0xD3, 0x9A);

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
    /// Writes a valid ICO file with one PNG-compressed frame per size in <see cref="IconSizes"/>,
    /// each rendered natively. PNG-in-ICO is supported by Windows Vista and later.
    /// </summary>
    private static void SaveAsIco(string filePath)
    {
        // Render each size natively (no downscaling) so small frames stay sharp.
        var frames = Array.ConvertAll(IconSizes, s =>
        {
            using var bmp = RenderIconBitmap(s);
            using var ms  = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        });

        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

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
    }
}
