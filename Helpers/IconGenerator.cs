using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace LenovoTray.Helpers;

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
    private const string IconVersion = "v2";

    internal static string GenerateAndSaveTrayIcon(string outputDirectory)
    {
        var icoPath = Path.Combine(outputDirectory, $"LenovoRed-{IconVersion}.ico");
        if (File.Exists(icoPath)) return icoPath;

        SaveAsIco(icoPath);
        return icoPath;
    }

    /// <summary>
    /// Renders a live battery-level tray icon as a 32×32 <see cref="System.Drawing.Icon"/>.
    /// The caller must call <see cref="NativeMethods.DestroyIcon"/> on the handle of the
    /// returned icon when it is no longer needed (before replacing it with a new one).
    /// Returns a generic battery icon when <paramref name="percent"/> is 0.
    /// </summary>
    internal static System.Drawing.Icon RenderBatteryIcon(int percent, bool charging)
    {
        using var bmp    = RenderBatteryBitmap(32, percent, charging);
        IntPtr    hIcon  = bmp.GetHicon();
        // Clone copies the icon data into a managed-owned handle; destroy the GDI original.
        var icon = (System.Drawing.Icon)System.Drawing.Icon.FromHandle(hIcon).Clone();
        NativeMethods.DestroyIcon(hIcon);
        return icon;
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
    /// centre 50/50, radius 38, 7-o'clock start (135°), 270° sweep — same proportions
    /// as the DashboardWindow gauge so the two visuals feel consistent.
    /// </summary>
    private static Bitmap RenderBatteryBitmap(int size, int percent, bool charging)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode   = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        // Virtual 100×100 canvas → scale to actual icon size.
        float scale  = size / 100f;
        float cx     = 50 * scale;
        float cy     = 50 * scale;
        float radius = 38 * scale;
        float stroke = Math.Max(2f, 7f * scale);   // proportional stroke

        // Track (background ring).
        using var trackPen = new System.Drawing.Pen(Color.FromArgb(60, 200, 200, 200), stroke);
        trackPen.StartCap = trackPen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
        DrawArc(g, trackPen, cx, cy, radius, 135f, 270f);

        if (percent > 0)
        {
            // Fill colour: green > 50%, orange 21-50%, red ≤ 20%.
            Color fillColor = percent switch
            {
                > 50 => Color.FromArgb(255, 0x10, 0xB9, 0x81),  // green
                > 20 => Color.FromArgb(255, 0xFF, 0x8C, 0x00),  // orange
                _    => Color.FromArgb(255, 0xE2, 0x00, 0x1A),  // red
            };
            if (charging) fillColor = Color.FromArgb(255, 0x10, 0xB9, 0x81); // always green when charging

            using var fillPen = new System.Drawing.Pen(fillColor, stroke);
            fillPen.StartCap = fillPen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
            DrawArc(g, fillPen, cx, cy, radius, 135f, 270f * percent / 100f);
        }

        // Small % text in the centre (only legible at 32 px and above).
        if (size >= 24)
        {
            string label = percent > 0 ? $"{percent}" : "?";
            float  fontSize = Math.Max(6f, size * 0.22f);
            using var font  = new Font("Segoe UI", fontSize, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(Color.FromArgb(220, 255, 255, 255));
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(label, font, brush, new RectangleF(0, 0, size, size), sf);
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
