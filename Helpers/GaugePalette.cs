using ZeroZero.Brand.Core;

namespace ChargeKeeper.Helpers;

/// <summary>One anchor on a gauge scale: the charge level and the colour the gauge reads exactly
/// there.</summary>
internal readonly record struct GaugeStop(int Percent, uint Argb);

/// <summary>
/// Framework-neutral source of truth for the charge-state gauge palette, shared by
/// <see cref="AppColors"/> (WinUI) and <see cref="IconGenerator"/> (GDI+). The two frameworks share
/// no Color type, but packed ARGB bytes cross that divide.
/// </summary>
/// <remarks>Three continuous scales, one per <see cref="PowerState"/>, interpolated in Oklab. A
/// tiered gauge painted three quarters of the normal battery range in a warning tone and stepped
/// between tiers; a scale carries the reading itself.</remarks>
internal static class GaugePalette
{
    /// <summary>An opaque packed 0xAARRGGBB value from a studio palette constant such as
    /// "#7fa8b8". The only place in the app that turns a brand hex string into bytes.</summary>
    internal static uint FromHex(string hex) =>
        0xFF000000u | Convert.ToUInt32(hex.TrimStart('#'), 16);

    // Packed 0xAARRGGBB. Three of these are studio palette colours and read their value from
    // ZeroZero.Brand.Core rather than restating it; the rest are ChargeKeeper's own and the shared
    // palette does not carry them. PaletteAdoptionTests pins the three against the studio values.
    internal const uint Ember     = 0xFFC2593F;   // deep flat below the draining scale
    internal const uint SageGreen = 0xFF7AB88F;   // comfortable on battery / brand-mark interior
    internal const uint Lavender  = 0xFF9C8FBD;   // near the top of both battery scales
    internal const uint Orchid    = 0xFFC2569B;   // held high on mains

    internal static readonly uint Terracotta = FromHex(Brand.ColorTerracotta);  // low on battery / charge-limit accent
    internal static readonly uint SteelBlue  = FromHex(Brand.ColorSteelBlue);   // connected to mains + app accent

    /// <summary>Brand amber. No gauge role: it read as a warning across most of the battery range.
    /// Still the discharging status glyph and the charge-limit tick marks.</summary>
    internal static readonly uint Amber = FromHex(Brand.ColorAmber);

    /// <summary>On battery. Runs from a deep ember at the bottom through terracotta and sage to
    /// lavender at a level a laptop rarely sits at unplugged.</summary>
    internal static IReadOnlyList<GaugeStop> Draining { get; } =
    [
        new(10, Ember),
        new(30, Terracotta),
        new(75, SageGreen),
        new(92, Lavender),
    ];

    /// <summary>Taking charge. Steel blue is the app's connected accent; the top end drifts to
    /// lavender as the pack fills.</summary>
    internal static IReadOnlyList<GaugeStop> Charging { get; } =
    [
        new(65, SteelBlue),
        new(85, Lavender),
    ];

    /// <summary>Connected and not charging. Departs from the charging scale at the top: orchid says
    /// the pack is being held high on mains, which is the state that wears it.</summary>
    internal static IReadOnlyList<GaugeStop> IdleOnMains { get; } =
    [
        new(80, SteelBlue),
        new(94, Orchid),
    ];

    /// <summary>The scale <paramref name="state"/> is painted on.</summary>
    internal static IReadOnlyList<GaugeStop> ScaleFor(PowerState state) => state switch
    {
        PowerState.Charging    => Charging,
        PowerState.IdleOnMains => IdleOnMains,
        _                      => Draining,
    };

    /// <summary>The gauge colour at <paramref name="percent"/> for <paramref name="state"/>.</summary>
    internal static uint FillFor(int percent, PowerState state) => Sample(ScaleFor(state), percent);

    /// <summary>Samples <paramref name="scale"/> at <paramref name="percent"/>: the anchor's own
    /// colour exactly at an anchor, an Oklab blend between the two it falls between, and flat below
    /// the first anchor and above the last.</summary>
    internal static uint Sample(IReadOnlyList<GaugeStop> scale, int percent)
    {
        ArgumentNullException.ThrowIfNull(scale);
        if (scale.Count == 0) throw new ArgumentException("A gauge scale carries at least one anchor.", nameof(scale));

        if (percent <= scale[0].Percent)  return scale[0].Argb;
        if (percent >= scale[^1].Percent) return scale[^1].Argb;

        for (int i = 0; i < scale.Count - 1; i++)
        {
            var (from, to) = (scale[i], scale[i + 1]);
            if (percent > to.Percent) continue;

            double t = (percent - from.Percent) / (double)(to.Percent - from.Percent);
            return Oklab.Mix(from.Argb, to.Argb, t);
        }

        return scale[^1].Argb;
    }
}
