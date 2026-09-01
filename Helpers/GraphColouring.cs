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
}
