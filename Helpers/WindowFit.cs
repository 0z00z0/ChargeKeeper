namespace ChargeKeeper.Helpers;

/// <summary>
/// Pure window-placement geometry (physical px): given the rect a window wants, the height it would
/// need to show its content without scrolling, and the work area it lands on, decide the rect it
/// actually opens at.
/// </summary>
internal static class WindowFit
{
    /// <summary>
    /// The final rect: grown to <paramref name="requiredHeight"/> when the content is taller, never
    /// wider or taller than <paramref name="workArea"/>, and always fully inside it.
    /// <para><paramref name="requiredHeight"/> is a WINDOW height, not a content height — only the
    /// caller can measure the title bar and chrome. Pass 0 to clamp without growing.</para>
    /// <para>A rect that misses the work area entirely is re-centred rather than clamped: clamping
    /// alone would jam it into the nearest corner.</para>
    /// </summary>
    internal static (int X, int Y, int W, int H) Fit(
        (int X, int Y, int W, int H) desired,
        int requiredHeight,
        (int X, int Y, int W, int H) workArea)
    {
        int w = Math.Min(desired.W, workArea.W);
        int h = Math.Min(Math.Max(desired.H, requiredHeight), workArea.H);

        // Half-open: a rect that merely touches the work-area edge shares no pixel with it, so it
        // counts as off-screen.
        bool onScreen = desired.X < workArea.X + workArea.W && desired.X + desired.W > workArea.X &&
                        desired.Y < workArea.Y + workArea.H && desired.Y + desired.H > workArea.Y;

        if (!onScreen)
            return (workArea.X + (workArea.W - w) / 2,
                    workArea.Y + (workArea.H - h) / 2,
                    w, h);

        return (Math.Clamp(desired.X, workArea.X, workArea.X + workArea.W - w),
                Math.Clamp(desired.Y, workArea.Y, workArea.Y + workArea.H - h),
                w, h);
    }

    /// <summary>
    /// The height a window must be for content of <paramref name="contentHeight"/> to show without
    /// scrolling, given that it is <paramref name="currentHeight"/> tall and shows
    /// <paramref name="viewportHeight"/> of that content today. The difference carries the title bar
    /// and the scroller's padding with it, so the chrome is never added up by hand. Unlike
    /// <see cref="Fit"/> this may also SHRINK, down to the <paramref name="minHeight"/> floor.
    /// <para>Unit-agnostic: all four values must be in the same unit, DIPs or physical px. Mixing
    /// them silently mis-sizes the window on any scaled display.</para>
    /// </summary>
    internal static int HeightForContent(double currentHeight, double contentHeight,
                                         double viewportHeight, int minHeight)
        => Math.Max(minHeight, (int)Math.Ceiling(currentHeight + contentHeight - viewportHeight));
}
