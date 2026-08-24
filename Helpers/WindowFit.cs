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

    // The Settings window's furniture that does not reflow, in DIPs. Everything else on the page —
    // the SettingsCard header text above all — wraps, so it sets no floor of its own.
    private const double ScrollBarGutterDip   = 16;   // the expanded vertical scrollbar
    private const double CardPaddingDip       = 32;   // SettingsCard's own 16 either side
    private const double WidestCardControlDip = 220;  // the widest MinWidth any card content carries
    private const double CardRowDip           = 52;   // the SettingsCardMinHeight the page overrides to
    private const double NavItemHeightDip     = 40;   // NavigationViewItemOnLeftMinHeight
    private const double NavPaneHeaderDip     = 48;   // the pane's toggle-button row
    private const double NavPaneFooterDip     = 69;   // divider, margins and the 36 DIP brand row
    private const double TitleBarDip          = 32;

    /// <summary>
    /// The narrowest the Settings window stays usable at, in DIPs: the navigation pane, which keeps
    /// its width whatever the window does, plus the content column's own fixed parts — the scroller's
    /// padding and scrollbar gutter, a card's padding, and the widest fixed-width control a card
    /// carries. Added up rather than picked round, so a chrome change moves it.
    /// </summary>
    internal static int MinimumWidthDip(double navPaneLength, double scrollerPadding) =>
        (int)Math.Ceiling(navPaneLength + scrollerPadding
                          + ScrollBarGutterDip + CardPaddingDip + WidestCardControlDip);

    /// <summary>
    /// The shortest it stays usable at, in DIPs. The navigation pane governs, not the content: the
    /// content scrolls, while every nav item and the pinned brand footer have to stay reachable.
    /// </summary>
    internal static int MinimumHeightDip(int navItemCount, double scrollerPadding) =>
        (int)Math.Ceiling(TitleBarDip + Math.Max(
            NavPaneHeaderDip + navItemCount * NavItemHeightDip + NavPaneFooterDip,
            scrollerPadding + CardRowDip));

    /// <summary>
    /// DIPs to physical pixels. <c>AppWindow</c> and its presenter are sized in physical pixels while
    /// XAML lays out in DIPs, so a minimum passed through unscaled is 43 % short on a 175 % panel.
    /// <para>The scale must come from the window's own <c>XamlRoot.RasterizationScale</c>.
    /// <c>GetDpiForSystem</c> returns 96 from a process that is not per-monitor aware, which reads as
    /// 100 % on every display and would quietly disable the scaling.</para>
    /// </summary>
    internal static int ToPhysicalPixels(int dip, double rasterizationScale) =>
        (int)Math.Ceiling(dip * (rasterizationScale > 0 ? rasterizationScale : 1.0));
}
