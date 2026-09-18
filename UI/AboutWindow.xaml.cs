using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using ChargeKeeper.Helpers;
using ChargeKeeper.Services;

namespace ChargeKeeper.UI;

/// <summary>
/// About window chrome, hosting the shared <c>BrandAboutControl</c> for the content. Frameless and
/// dismissed like the pop-out graph: it closes itself on focus loss or Escape. Single instance
/// owned by <see cref="TrayMenu"/>; the Settings window embeds the same control inline.
/// </summary>
internal sealed partial class AboutWindow : Window
{
    // How much larger the card reads than its shared design width, applied uniformly by the
    // Viewbox in the XAML so both dimensions (and every hard-coded font size inside the card,
    // which the brand package never exposes as a resource) grow together.
    private const double CardScale = 1.2;

    // A floor, not a target — the height is measured from the content in FitWindowToContent.
    private const int MinHeightDip = 320;

    // Window width in DIPs: the scaled card plus the scroller's own left/right padding, read live
    // rather than duplicating the 24 DIP set in XAML. Computed once InitializeComponent has run,
    // since ContentScroller.Padding is not set before then.
    private readonly int _windowWidthDip;

    private bool _placed;

    // A Deactivated before the first real Activated is spurious — the window hasn't finished taking
    // focus, and treating it as a dismissal would close the window as it opens.
    private bool _everActivated;

    // Latched on the first dismissal or on Closed, so nothing closes a window twice.
    private bool _closing;

    // Set while the update dialog this window owns is up. The dialog takes focus, and closing its
    // owner underneath it would take the dialog down with it.
    private bool _dialogOpen;

    private readonly UpdateCheckButtonController _updateButton;

    /// <param name="menu">Owns the "What's new" window and the update flow, so this window creates
    /// neither of its own.</param>
    public AboutWindow(TrayMenu menu)
    {
        InitializeComponent();
        Title = "About ChargeKeeper";

        WindowChrome.ApplyPopup(this, resizable: false, alwaysOnTop: false);
        // A no-op on this frameless popup, but keeps the call site uniform with the other windows.
        ChargeKeeper.Helpers.TitleBarTheme.ApplyDark(AppWindow);

        // Fix the card at its shared design width, then tell the Viewbox the scaled width to grow
        // it to; Height is left unset so it derives from the child's natural aspect at that width.
        About.Width = AboutContent.ContentWidthDip;
        AboutScaler.Width = AboutContent.ContentWidthDip * CardScale;
        _windowWidthDip = (int)Math.Ceiling(AboutScaler.Width
                         + ContentScroller.Padding.Left + ContentScroller.Padding.Right);

        About.SetInfo(AboutContent.Build(menu.ShowWhatsNew));
        _updateButton = new UpdateCheckButtonController(CheckForUpdatesButton, menu, HoldOpenAround);

        // Placed on first activation, once the content is in a live visual tree and can be measured.
        Activated += OnActivated;
        Closed    += (_, _) =>
        {
            _closing = true;
            _updateButton.Detach();
        };
    }

    /// <summary>The check run each time the window is shown; its outcome shows on the button alone.</summary>
    internal void CheckForUpdatesAutomatically() => _updateButton.CheckAutomatically();

    private void HoldOpenAround(Action showDialog)
    {
        _dialogOpen = true;
        try { showDialog(); }
        finally { _dialogOpen = false; }
    }

    /// <summary>Dismisses on focus loss; places and sizes the window once, centred on the monitor
    /// under the cursor, on the first real activation.</summary>
    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState == WindowActivationState.Deactivated)
        {
            if (!_everActivated) return;   // spurious pre-activation deactivate — see field doc
            if (_dialogOpen) return;       // focus went to this window's own update dialog
            Dismiss();
            return;
        }

        _everActivated = true;

        if (_placed) return;
        _placed = true;

        try
        {
            // Width first, measure second: the height is only real at the width it will be shown at.
            AppWindow.MoveAndResize(NativeMethods.CentreRectOnCursorMonitor(_windowWidthDip, MinHeightDip));
            ContentScroller.UpdateLayout();
            FitWindowToContent();
        }
        catch (Exception ex) { AppLog.Error("AboutWindow.MoveAndResize", ex); }
    }

    /// <summary>Escape takes the same path as clicking away, so the key introduces no third
    /// behaviour of its own.</summary>
    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Dismiss();
    }

    /// <summary>Closes the window. Close, not Hide: <see cref="TrayMenu"/> recreates it on the next
    /// open, and with no title bar there is no other dismissal.</summary>
    private void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    /// <summary>
    /// Sizes the window to the measured About content, the same way <c>SettingsWindow</c> sizes
    /// itself to its tallest page: grown to fit, capped at <see cref="WindowFit.FirstOpenHeightFraction"/>
    /// of the work area, and re-centred within it — the scroller already on this window is the
    /// fallback for the content that does not fit under the cap, not the usual case.
    /// </summary>
    private void FitWindowToContent()
    {
        double viewport = ContentScroller.ViewportHeight;
        if (viewport <= 0 || ContentPanel.ActualWidth <= 0) return;   // not laid out yet — keep the opening rect

        // The panel, not the shared control alone: the update button below it is part of what the
        // window has to be tall enough for.
        ContentPanel.Measure(new Windows.Foundation.Size(ContentPanel.ActualWidth, double.PositiveInfinity));
        double content = ContentPanel.DesiredSize.Height
                       + ContentScroller.Padding.Top + ContentScroller.Padding.Bottom;

        // AppWindow.Size/Position are physical px while everything measured above is DIPs.
        double scale = Content.XamlRoot?.RasterizationScale ?? 1.0;
        var pos      = AppWindow.Position;
        var size     = AppWindow.Size;

        if (NativeMethods.WorkAreaForRect(pos.X, pos.Y, size.Width, size.Height) is not { } work) return;

        int heightDip = WindowFit.HeightForContent(size.Height / scale, content, viewport, MinHeightDip);
        int heightPx  = Math.Min(WindowFit.ToPhysicalPixels(heightDip, scale),
                                 WindowFit.FirstOpenHeightCap(work.H));
        int widthPx   = Math.Min(WindowFit.ToPhysicalPixels(_windowWidthDip, scale), work.W);

        AppLog.Info($"AboutWindow fit: content={content:F0} viewport={viewport:F0} scale={scale} " +
                    $"-> {heightDip} DIP, capped {heightPx}x{widthPx} px in work area {work.W}x{work.H}");

        var rect = new RectInt32(work.X + (work.W - widthPx) / 2, work.Y + (work.H - heightPx) / 2,
                                 widthPx, heightPx);
        AppWindow.MoveAndResize(rect);
    }
}
