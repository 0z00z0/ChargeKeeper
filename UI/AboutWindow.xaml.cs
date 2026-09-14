using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
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
    private const int WidthDip = AboutContent.ContentWidthDip;

    // A floor, not a target — the height is measured from the content in FitWindowToContent.
    private const int MinHeightDip = 320;

    private bool _placed;

    // A Deactivated before the first real Activated is spurious — the window hasn't finished taking
    // focus, and treating it as a dismissal would close the window as it opens.
    private bool _everActivated;

    // Latched on the first dismissal or on Closed, so nothing closes a window twice.
    private bool _closing;

    /// <summary>Opens the "What's new" report. Supplied by the caller that owns that window, so
    /// this one creates nothing of its own.</summary>
    private readonly Action? _showWhatsNew;

    public AboutWindow(Action? showWhatsNew = null)
    {
        _showWhatsNew = showWhatsNew;
        InitializeComponent();
        Title = "About ChargeKeeper";

        WindowChrome.ApplyPopup(this, resizable: false, alwaysOnTop: false);
        // A no-op on this frameless popup, but keeps the call site uniform with the other windows.
        ChargeKeeper.Helpers.TitleBarTheme.ApplyDark(AppWindow);

        About.SetInfo(AboutContent.Build());
        WhatsNewButton.Visibility = _showWhatsNew is null ? Visibility.Collapsed : Visibility.Visible;

        // Placed on first activation, once the content is in a live visual tree and can be measured.
        Activated += OnActivated;
        Closed    += (_, _) => _closing = true;
    }

    private void OnWhatsNew(object sender, RoutedEventArgs e) => _showWhatsNew?.Invoke();

    /// <summary>Dismisses on focus loss; places and sizes the window once, centred on the monitor
    /// under the cursor, on the first real activation.</summary>
    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState == WindowActivationState.Deactivated)
        {
            if (!_everActivated) return;   // spurious pre-activation deactivate — see field doc
            Dismiss();
            return;
        }

        _everActivated = true;

        if (_placed) return;
        _placed = true;

        try
        {
            // Width first, measure second: the height is only real at the width it will be shown at.
            AppWindow.MoveAndResize(NativeMethods.CentreRectOnCursorMonitor(WidthDip, MinHeightDip));
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
    /// Sizes the window to the measured About content. The chrome is not added up: it is exactly what
    /// the window height and the viewport height differ by, so measuring that difference covers it.
    /// </summary>
    private void FitWindowToContent()
    {
        double viewport = ContentScroller.ViewportHeight;
        if (viewport <= 0 || ContentPanel.ActualWidth <= 0) return;   // not laid out yet — keep the opening rect

        // The panel, not the shared control alone: the "What's new" button below it is part of what
        // the window has to be tall enough for.
        ContentPanel.Measure(new Windows.Foundation.Size(ContentPanel.ActualWidth, double.PositiveInfinity));
        double content = ContentPanel.DesiredSize.Height
                       + ContentScroller.Padding.Top + ContentScroller.Padding.Bottom;

        // AppWindow.Size is physical px while everything measured above is DIPs.
        double scale  = Content.XamlRoot?.RasterizationScale ?? 1.0;
        int heightDip = WindowFit.HeightForContent(AppWindow.Size.Height / scale, content, viewport, MinHeightDip);

        AppLog.Info($"AboutWindow fit: content={content:F0} viewport={viewport:F0} scale={scale} " +
                    $"-> {heightDip} DIP");

        AppWindow.MoveAndResize(NativeMethods.CentreRectOnCursorMonitor(WidthDip, heightDip));
    }
}
