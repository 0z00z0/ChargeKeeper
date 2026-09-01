namespace ChargeKeeper.Helpers;

/// <summary>Which way charge is actually moving, taken from the measured battery wattage rather
/// than the reported power state. An adapter that supplies less than the machine draws leaves the
/// state reading as charging while the pack falls; only the sign of the rate sees that.</summary>
internal enum PowerFlow
{
    /// <summary>The pack is gaining charge.</summary>
    In,

    /// <summary>The pack is losing charge, whether or not an adapter is connected.</summary>
    Out,

    /// <summary>No meaningful flow either way.</summary>
    Rest,
}

/// <summary>Derives <see cref="PowerFlow"/> from a charge rate, and names the three marks that
/// stand for it. The marks are the dashboard's status glyphs, so the tray and the dashboard say
/// the same thing with the same shapes.</summary>
internal static class PowerFlows
{
    /// <summary>
    /// Rates smaller than this in magnitude count as no flow. Some packs report a clean zero at
    /// rest and others a small trickle, so the band cannot be zero-width; 100 mW sits below the
    /// smallest genuine rate seen in recorded history and is the same figure the remaining-time
    /// estimates already treat as too small to divide by.
    /// </summary>
    internal const int RestBandMw = 100;

    /// <summary>Upward mark: gaining charge.</summary>
    internal const string GlyphIn = "▲";

    /// <summary>Downward mark: losing charge.</summary>
    internal const string GlyphOut = "▼";

    /// <summary>Dot: at rest.</summary>
    internal const string GlyphRest = "●";

    /// <summary>
    /// The flow a rate implies, or null when there is no reading. Null must reach the icon as
    /// "draw nothing": an absent rate is not a rate of zero, and a symbol drawn from one would
    /// claim a direction the device never reported.
    /// </summary>
    internal static PowerFlow? From(int? milliwatts)
    {
        if (milliwatts is not { } mw) return null;
        // Compared as bounds rather than Math.Abs, which overflows on int.MinValue.
        if (mw > -RestBandMw && mw < RestBandMw) return PowerFlow.Rest;
        return mw > 0 ? PowerFlow.In : PowerFlow.Out;
    }

    /// <summary>The mark that stands for <paramref name="flow"/>.</summary>
    internal static string Glyph(PowerFlow flow) => flow switch
    {
        PowerFlow.In  => GlyphIn,
        PowerFlow.Out => GlyphOut,
        _             => GlyphRest,
    };
}
