using System;
using Microsoft.UI.Xaml;
using ChargeKeeper.Helpers;
using ChargeKeeper.Services;

namespace ChargeKeeper.UI;

/// <summary>
/// #59: ChargeKeeper's own About window. Owns only the window chrome (Mica BaseAlt backdrop,
/// sizing, close) and hosts the shared <see cref="ZeroZero.Brand.WinUI.BrandAboutControl"/> for
/// the actual content — brand header, description, repo/website/donate links and the external-
/// libraries credit list. The shared control deliberately has no "Check for updates" button;
/// that stays on the tray menu (<c>TrayMenu.CheckForUpdatesAsync</c>).
///
/// <para>Single reusable instance owned by <see cref="TrayMenu"/> (its <c>_aboutWindow</c>
/// field). This window is now the TRAY's entry point only — the Settings window embeds the same
/// <c>BrandAboutControl</c> inline rather than opening a second dialog on top of itself. Both
/// surfaces share one payload (<see cref="AboutContent.Build"/>) so they cannot drift.</para>
/// </summary>
internal sealed partial class AboutWindow : Window
{
    // Target width in DIPs; scaled to the physical pixels of whichever monitor it lands on. It is the
    // shared control's own layout measure (the Settings About card is capped to the same number), so
    // it lives with the payload rather than being re-stated per host.
    private const int WidthDip = AboutContent.ContentWidthDip;

    // A floor, not a target — the height is measured from the content in FitWindowToContent. Roughly
    // the brand header plus the link row: anything shorter reads as a sliver rather than a dialog.
    private const int MinHeightDip = 320;

    private bool _placed;

    public AboutWindow()
    {
        InitializeComponent();
        Title = "About ChargeKeeper";

        // Dark-theme the standard title bar so it matches the Mica BaseAlt backdrop.
        ChargeKeeper.Helpers.TitleBarTheme.ApplyDark(AppWindow);

        About.SetInfo(AboutContent.Build());

        // Sized and placed on first activation, once the content is in a live visual tree and can be
        // measured. The libraries expander changes the height afterwards; the ScrollViewer absorbs
        // that, so the window is measured once and then left at the size the user sees.
        Activated += OnActivated;
    }

    /// <summary>
    /// Places the window once, on first activation: centred on the monitor under the cursor — the
    /// one the user just used the tray menu on — sized for THAT monitor's scaling, and only as tall
    /// as the content actually is. Guarded: a placement failure must never stop the window from
    /// showing.
    /// </summary>
    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (_placed) return;
        _placed = true;

        try
        {
            // Width first, measure second: the content only reports the height it will really take
            // once it is laying out at the width it will really be shown at.
            AppWindow.MoveAndResize(NativeMethods.CentreRectOnCursorMonitor(WidthDip, MinHeightDip));
            ContentScroller.UpdateLayout();
            FitWindowToContent();
        }
        catch (Exception ex) { AppLog.Error("AboutWindow.MoveAndResize", ex); }
    }

    /// <summary>
    /// Sizes the window to the measured About content, replacing a hard-coded height that left a
    /// large empty region below the copyright line and would have needed re-tuning by hand every
    /// time a library was credited.
    ///
    /// <para>The chrome — title bar plus the scroller's padding — is not added up here: it is exactly
    /// what the window height and the viewport height differ by, so measuring that difference picks
    /// all of it up. Same trick as <c>SettingsWindow.FitWindowToContent</c>, which only ever grows;
    /// this window has to shrink.</para>
    /// </summary>
    private void FitWindowToContent()
    {
        double viewport = ContentScroller.ViewportHeight;
        if (viewport <= 0 || About.ActualWidth <= 0) return;   // not laid out yet — keep the opening rect

        About.Measure(new Windows.Foundation.Size(About.ActualWidth, double.PositiveInfinity));
        double content = About.DesiredSize.Height
                       + ContentScroller.Padding.Top + ContentScroller.Padding.Bottom;

        // AppWindow.Size is physical px while everything measured above is DIPs — unscaled, this is
        // 75% short on the 175% laptop panel.
        double scale  = Content.XamlRoot?.RasterizationScale ?? 1.0;
        int heightDip = WindowFit.HeightForContent(AppWindow.Size.Height / scale, content, viewport, MinHeightDip);

        AppLog.Info($"AboutWindow fit: content={content:F0} viewport={viewport:F0} scale={scale} " +
                    $"-> {heightDip} DIP");

        // Re-centres and caps to the work area, so a payload taller than the screen scrolls rather
        // than hanging off the bottom.
        AppWindow.MoveAndResize(NativeMethods.CentreRectOnCursorMonitor(WidthDip, heightDip));
    }
}
