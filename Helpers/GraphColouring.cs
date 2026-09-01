using ChargeKeeper.Services;

namespace ChargeKeeper.Helpers;

/// <summary>
/// What the battery history graph's charge line is drawn in, and whether the fade beneath it is
/// drawn at all. Renderer-free: colours are packed 0xAARRGGBB, exactly as
/// <see cref="GaugePalette"/> produces them, and the two decisions are taken from two settings that
/// never consult each other.
/// </summary>
internal static class GraphColouring
{
    /// <summary>A stored value naming no member of the enum, resolved to the default. Settings enums
    /// round-trip as strings but the converter also accepts integers, so a hand-edited number lands
    /// here undefined instead of failing the whole file's load.</summary>
    internal static GraphLineColouring Normalise(GraphLineColouring mode) =>
        Enum.IsDefined(mode) ? mode : GraphLineColouring.OneColour;

    /// <summary>True when the line's colour changes from point to point, so the caller builds a
    /// gradient along the series rather than taking one solid brush.</summary>
    internal static bool VariesByPoint(GraphLineColouring mode) =>
        Normalise(mode) != GraphLineColouring.OneColour;

    /// <summary>
    /// The colour one history point contributes to the charge line. <paramref name="accent"/> is the
    /// line's fixed colour and is returned whenever nothing else is known: the one-colour setting, a
    /// setting outside the enum, and a point carrying no recorded power state — history written
    /// before the state was stored is left as it has always looked rather than painted as draining.
    /// </summary>
    internal static uint LineColourFor(GraphLineColouring mode, int soc, PowerState? state, uint accent) =>
        Normalise(mode) switch
        {
            // No state is being claimed here, so the on-battery scale is named directly.
            GraphLineColouring.ByLevel         => GaugePalette.Sample(GaugePalette.Draining, soc),
            GraphLineColouring.ByLevelAndState => state is { } recorded
                                                      ? GaugePalette.FillFor(soc, recorded)
                                                      : accent,
            _                                  => accent,
        };

    /// <summary>Whether the fade beneath the line is drawn. <paramref name="mode"/> is taken and
    /// deliberately not read: the two controls are independent, and the fade keeps the accent
    /// whatever the line is coloured by.</summary>
    internal static bool ShouldShade(GraphLineColouring mode, bool shadingEnabled) => shadingEnabled;

    /// <summary>One gradient stop along the charge line, renderer-free: an offset in 0–1 and a packed
    /// 0xAARRGGBB colour.</summary>
    internal readonly record struct LineStop(double Offset, uint Argb);

    /// <summary>
    /// The gradient stops for one continuous run of the charge line. <paramref name="indices"/> names
    /// the run's points within <paramref name="samples"/> and <paramref name="xs"/>, in order.
    /// Offsets are normalised across the run's own x extent, because the brush paints that run's path
    /// and is mapped to the path's bounding box: normalising against the whole plot instead leaves
    /// the gradient spanning far more than the stroke, which draws the line in its first stop's
    /// colour alone. Stops are strided to <paramref name="maxStops"/>, always closing on the run's
    /// final point so striding cannot leave the right edge on an extrapolated colour.
    /// </summary>
    internal static IReadOnlyList<LineStop> LineStops(
        GraphLineColouring mode, IReadOnlyList<BatterySample> samples, IReadOnlyList<double> xs,
        IReadOnlyList<int> indices, uint accent, int maxStops)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(xs);
        ArgumentNullException.ThrowIfNull(indices);
        if (indices.Count == 0) return [];

        double first = xs[indices[0]];
        double span  = xs[indices[^1]] - first;

        var stops = new List<LineStop>();
        int step = Math.Max(1, indices.Count / Math.Max(1, maxStops));
        for (int k = 0; k < indices.Count; k += step) stops.Add(StopAt(k));
        int last = indices.Count - 1;
        if (last % step != 0) stops.Add(StopAt(last));
        return stops;

        // A run pinned to one instant has no extent to spread across, so every stop sits at 0 and the
        // renderer takes the last one; still a valid single-colour brush.
        LineStop StopAt(int k)
        {
            int i = indices[k];
            return new(span > 0 ? Math.Clamp((xs[i] - first) / span, 0, 1) : 0,
                       LineColourFor(mode, samples[i].Soc, samples[i].State, accent));
        }
    }
}
