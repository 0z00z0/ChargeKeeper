using System;
using Microsoft.UI.Xaml;
using ChargeKeeper.Helpers;
using ChargeKeeper.Services;

namespace ChargeKeeper.UI;

/// <summary>
/// About window chrome, hosting the shared <c>BrandAboutControl</c> for the content. Single instance
/// owned by <see cref="TrayMenu"/>; the Settings window embeds the same control inline.
/// </summary>
internal sealed partial class AboutWindow : Window
{
    private const int WidthDip = AboutContent.ContentWidthDip;

    // A floor, not a target — the height is measured from the content in FitWindowToContent.
    private const int MinHeightDip = 320;

    private bool _placed;

    /// <summary>Opens the "What's new" report. Supplied by the caller that owns that window, so
    /// this one creates nothing of its own.</summary>
    private readonly Action? _showWhatsNew;

    public AboutWindow(Action? showWhatsNew = null)
    {
        _showWhatsNew = showWhatsNew;
        InitializeComponent();
        Title = "About ChargeKeeper";

        ChargeKeeper.Helpers.TitleBarTheme.ApplyDark(AppWindow);

        About.SetInfo(AboutContent.Build());
        WhatsNewButton.Visibility = _showWhatsNew is null ? Visibility.Collapsed : Visibility.Visible;

        // Placed on first activation, once the content is in a live visual tree and can be measured.
        Activated += OnActivated;
    }

    private void OnWhatsNew(object sender, RoutedEventArgs e) => _showWhatsNew?.Invoke();

    /// <summary>Places and sizes the window once, centred on the monitor under the cursor.</summary>
    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
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
