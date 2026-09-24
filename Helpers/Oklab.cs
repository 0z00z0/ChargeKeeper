namespace ChargeKeeper.Helpers;

/// <summary>A colour in the Oklab perceptual space: lightness and two opponent axes.</summary>
internal readonly record struct OklabColor(double L, double A, double B);

/// <summary>
/// Conversion between packed 0xAARRGGBB sRGB and Oklab, and interpolation in that space. Kept apart
/// from every renderer: neither WinUI nor GDI+ appears here, so the maths is testable on its own.
/// </summary>
/// <remarks>Blending two muted tones straight in sRGB drags the midpoint towards grey, because the
/// channels are gamma-encoded and the space is not perceptually uniform. Oklab is, so a midpoint
/// keeps the chroma of its neighbours.</remarks>
internal static class Oklab
{
    /// <summary>Oklab for the RGB channels of <paramref name="argb"/>; the alpha byte is ignored.</summary>
    internal static OklabColor FromArgb(uint argb)
    {
        double r = ToLinear(((argb >> 16) & 0xFF) / 255.0);
        double g = ToLinear(((argb >>  8) & 0xFF) / 255.0);
        double b = ToLinear(( argb        & 0xFF) / 255.0);

        double l = 0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b;
        double m = 0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b;
        double s = 0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b;

        double lc = Math.Cbrt(l), mc = Math.Cbrt(m), sc = Math.Cbrt(s);

        return new(
            0.2104542553 * lc + 0.7936177850 * mc - 0.0040720468 * sc,
            1.9779984951 * lc - 2.4285922050 * mc + 0.4505937099 * sc,
            0.0259040371 * lc + 0.7827717662 * mc - 0.8086757660 * sc);
    }

    /// <summary>Packed 0xAARRGGBB for <paramref name="colour"/>, carrying
    /// <paramref name="alpha"/>. Channels outside the sRGB gamut are clamped.</summary>
    internal static uint ToArgb(OklabColor colour, byte alpha)
    {
        double lc = colour.L + 0.3963377774 * colour.A + 0.2158037573 * colour.B;
        double mc = colour.L - 0.1055613458 * colour.A - 0.0638541728 * colour.B;
        double sc = colour.L - 0.0894841775 * colour.A - 1.2914855480 * colour.B;

        double l = lc * lc * lc, m = mc * mc * mc, s = sc * sc * sc;

        double r =  4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s;
        double g = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s;
        double b = -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s;

        return ((uint)alpha << 24) | ((uint)Channel(r) << 16) | ((uint)Channel(g) << 8) | Channel(b);
    }

    /// <summary>
    /// Blends <paramref name="from"/> towards <paramref name="to"/> in Oklab, moving lightness and
    /// chroma on a straight line each and hue around its own circle by the shorter way.
    /// <paramref name="t"/> is clamped to 0..1, so 0 returns the first colour exactly and 1 the
    /// second. Alpha is blended on its own linear ramp.
    /// </summary>
    /// <remarks>A straight line between the two <em>axes</em> — the earlier shape of this method —
    /// cuts through the region nearer the neutral point than either endpoint whenever the two hues
    /// are far apart, so a wide-hue pair (an orange anchor and a green one, say) dipped towards grey
    /// exactly at its midpoint. Moving hue around the circle instead keeps chroma close to both
    /// endpoints' the whole way across.</remarks>
    internal static uint Mix(uint from, uint to, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        if (t <= 0.0) return from;
        if (t >= 1.0) return to;

        var a = FromArgb(from);
        var b = FromArgb(to);

        byte alpha = (byte)Math.Round(Lerp((from >> 24) & 0xFF, (to >> 24) & 0xFF, t));

        double chromaFrom = Math.Sqrt(a.A * a.A + a.B * a.B);
        double chromaTo   = Math.Sqrt(b.A * b.A + b.B * b.B);
        double chroma     = Lerp(chromaFrom, chromaTo, t);

        double hueFrom = Math.Atan2(a.B, a.A);
        double hueTo   = Math.Atan2(b.B, b.A);
        double hue     = hueFrom + ShortestHueStep(hueFrom, hueTo) * t;

        return ToArgb(
            new(Lerp(a.L, b.L, t), chroma * Math.Cos(hue), chroma * Math.Sin(hue)),
            alpha);
    }

    private static double Lerp(double from, double to, double t) => from + (to - from) * t;

    // atan2 returns a value in (-π, π], so the raw difference already sits inside (-2π, 2π) — one
    // fold either way is enough to land the shorter arc in (-π, π].
    private static double ShortestHueStep(double from, double to)
    {
        double delta = to - from;
        if (delta >  Math.PI) delta -= 2 * Math.PI;
        if (delta < -Math.PI) delta += 2 * Math.PI;
        return delta;
    }

    // The sRGB transfer function and its inverse. The linear segment near black is part of the
    // standard, not an approximation of the exponent.
    private static double ToLinear(double channel) =>
        channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);

    private static double FromLinear(double channel) =>
        channel <= 0.0031308 ? channel * 12.92 : 1.055 * Math.Pow(channel, 1.0 / 2.4) - 0.055;

    private static byte Channel(double linear) =>
        (byte)Math.Clamp(Math.Round(FromLinear(linear) * 255.0), 0, 255);
}
