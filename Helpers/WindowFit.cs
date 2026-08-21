namespace ChargeKeeper.Helpers;

/// <summary>
/// Pure window-placement geometry (physical px): given the rect a window wants, the height it would
/// need to show its content without scrolling, and the work area it lands on, decide the rect it
/// actually opens at. Extracted from <c>SettingsWindow</c> so the rules are unit-testable without a
/// live WinUI window — the same split as <c>DashboardIdlePolicy</c>.
/// </summary>
internal static class WindowFit
{
    /// <summary>
    /// The final rect: grown to <paramref name="requiredHeight"/> when the content is taller,
    /// never wider or taller than <paramref name="workArea"/>, and always fully inside it.
    ///
    /// <para><paramref name="requiredHeight"/> is a WINDOW height, not a content height — the caller
    /// has already added the title bar and chrome, because only it can measure them. Pass 0 to clamp
    /// without growing (the on-close save path, which must not resize what the user chose).</para>
    ///
    /// <para>The work area is a hard ceiling, so a page taller than the screen still opens with a
    /// scrollbar. That case is accepted deliberately: a window taller than the work area would push
    /// its own controls behind the taskbar, which is worse than scrolling.</para>
    ///
    /// <para>A rect that misses the work area entirely is re-centred rather than clamped. Clamping
    /// alone would slide it to the nearest edge, which is where a rect saved on a since-disconnected
    /// monitor lands — technically on screen, but jammed into a corner.</para>
    /// </summary>
    internal static (int X, int Y, int W, int H) Fit(
        (int X, int Y, int W, int H) desired,
        int requiredHeight,
        (int X, int Y, int W, int H) workArea)
    {
        int w = Math.Min(desired.W, workArea.W);
        int h = Math.Min(Math.Max(desired.H, requiredHeight), workArea.H);

        // Half-open overlap test: a rect that merely touches the work-area edge shares no pixel with
        // it, so it counts as off-screen and is re-centred rather than clamped to a zero-width sliver.
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
    /// <paramref name="viewportHeight"/> of that content today.
    ///
    /// <para>The over- or under-shoot carries the title bar and the scroller's padding with it, so
    /// the chrome is never added up by hand and cannot drift when it changes. <see cref="Fit"/> uses
    /// the same difference, but clamps it to growth only; this one must also SHRINK — a hard-coded
    /// About height left the bottom of that window empty. <paramref name="minHeight"/> is the floor,
    /// so a short payload cannot collapse the window to a sliver.</para>
    ///
    /// <para>Unit-agnostic: all four values must be in the same unit, DIPs or physical px. Mixing
    /// them silently mis-sizes the window on any scaled display.</para>
    /// </summary>
    internal static int HeightForContent(double currentHeight, double contentHeight,
                                         double viewportHeight, int minHeight)
        => Math.Max(minHeight, (int)Math.Ceiling(currentHeight + contentHeight - viewportHeight));
}
