namespace ChargeKeeper.Helpers;

/// <summary>
/// Pure column arithmetic for the dashboard's preset buttons: how many equal-width columns the
/// available width holds, and how many rows the presets then need. Kept out of the window so the
/// decision is testable without a laid-out grid.
/// </summary>
internal static class PresetButtonLayout
{
    /// <summary>
    /// Narrowest button that still identifies a preset: nine Cascadia Mono glyphs at the buttons'
    /// FontSize 11 (advance ~0.6 em, so ~60 px) plus their 6 px padding on each side. Every preset
    /// name in use is eight characters or fewer, so at this width only an unusually long one is
    /// trimmed — and the full label is on the tooltip either way.
    /// </summary>
    internal const double MinButtonWidth = 72;

    /// <summary>The gap between buttons, horizontally and vertically.</summary>
    internal const double Spacing = 4;

    /// <summary>
    /// One row wherever the presets fit at <see cref="MinButtonWidth"/> or wider — the popup is
    /// compact and every extra row costs height. Columns never exceed the preset count, so a single
    /// row is never padded out with empty cells.
    /// </summary>
    internal static (int Columns, int Rows) Choose(int count, double availableWidth)
    {
        if (count <= 0) return (0, 0);

        // n columns need n buttons plus n-1 gaps, which rearranges to this once a gap is added to
        // both sides of the division.
        int fit     = (int)((availableWidth + Spacing) / (MinButtonWidth + Spacing));
        int columns = Math.Clamp(fit, 1, count);
        return (columns, (count + columns - 1) / columns);
    }
}
