using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using ChargeKeeper.Services;

namespace ChargeKeeper.Helpers;

/// <summary>
/// Generates the red Lenovo "L" tray icon at runtime.
/// Writing to a file on disk lets H.NotifyIcon reload the icon if it is recreated,
/// and avoids the GDI handle leak that <c>Bitmap.GetHicon()</c> introduces.
/// </summary>
internal static class IconGenerator
{
    // Geometry as fractions of the icon size, so every frame is rendered natively and
    // stays crisp at small sizes. The "L" is drawn as two bars rather than a font glyph
    // (font hinting/antialiasing turns mushy at 16 px).
    private const float CornerRadiusFraction = 0.18f; // rounded-square corner radius
    private const float MarginFraction        = 0.04f; // gap from icon edge to red square
    private const float BarThicknessFraction  = 0.17f; // stroke width of the "L"
    private const float LetterHeightFraction  = 0.58f; // vertical extent of the "L"
    private const float LetterWidthFraction   = 0.44f; // foot width of the "L"

    // Sizes baked into the .ico — covers 100/125/150/200% tray DPI without upscaling.
    private static readonly int[] IconSizes = [32, 24, 20, 16];

    // Lenovo brand red: #E2001A
    private static readonly Color LenovoRed = Color.FromArgb(0xE2, 0x00, 0x1A);

    /// <summary>
    /// Generates a multi-size ICO file and returns its path.
    /// The file is created once; subsequent calls return the cached path immediately.
    /// </summary>
    // Version stamp baked into the filename so an in-place app update regenerates the icon
    // automatically rather than serving the stale cached file from a previous version.
    private const string IconVersion = "v4";

    internal static string GenerateAndSaveTrayIcon(string outputDirectory)
    {
        var icoPath = Path.Combine(outputDirectory, $"LenovoRed-{IconVersion}.ico");
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
            _    => Color.FromArgb(255, 0xE2, 0x00, 0x1A),
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

    private static Bitmap RenderIconBitmap(int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode     = SmoothingMode.AntiAlias;
        g.PixelOffsetMode   = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        // Rounded red square background (radius/margin scale with the icon).
        int margin = Math.Max(1, (int)Math.Round(size * MarginFraction));
        var rect   = new Rectangle(margin, margin, size - margin * 2 - 1, size - margin * 2 - 1);
        int radius = Math.Max(2, (int)Math.Round(size * CornerRadiusFraction));
        using (var bg   = new SolidBrush(LenovoRed))
        using (var path = BuildRoundedRectPath(rect, radius))
            g.FillPath(bg, path);

        // White "L": a vertical stem plus a bottom foot, centred as one bounding box.
        float thickness = size * BarThicknessFraction;
        float height    = size * LetterHeightFraction;
        float width     = size * LetterWidthFraction;
        float x0        = (size - width)  / 2f;
        float y0        = (size - height) / 2f;

        using var white = new SolidBrush(Color.White);
        g.FillRectangle(white, x0, y0, thickness, height);                       // vertical stem
        g.FillRectangle(white, x0, y0 + height - thickness, width, thickness);   // bottom foot

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
                _    => Color.FromArgb(255, 0xE2, 0x00, 0x1A),  // red
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
