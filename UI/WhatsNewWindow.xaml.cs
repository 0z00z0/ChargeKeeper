using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using ChargeKeeper.Helpers;
using ChargeKeeper.Services;

namespace ChargeKeeper.UI;

/// <summary>
/// What each release changed, newest first, with the running version at the top. Single instance
/// owned by <see cref="TrayMenu"/>, which is also what opens it after an update and from the About
/// surfaces — so the report is reachable at any time and not only once.
/// </summary>
internal sealed partial class WhatsNewWindow : Window
{
    private const int WidthDip     = AboutContent.ContentWidthDip;
    private const int MinHeightDip = 320;

    private bool _placed;

    public WhatsNewWindow()
    {
        InitializeComponent();
        Title = "What's new in ChargeKeeper";

        TitleBarTheme.ApplyDark(AppWindow);
        Build();

        Activated += OnActivated;
    }

    /// <summary>Fills the panel from the notes the application ships. Never throws: an empty or
    /// unreadable file leaves a window that says so rather than no window at all.</summary>
    private void Build()
    {
        try
        {
            var notes   = ReleaseNotes.All;
            string here = AppInfo.Version;

            if (notes.Count == 0)
            {
                Content.Children.Add(Body("No release notes shipped with this build."));
                return;
            }

            foreach (var note in notes)
            {
                bool running = string.Equals(note.Version, here, StringComparison.OrdinalIgnoreCase);

                var heading = new TextBlock
                {
                    Text    = running ? $"Version {note.Version} — running now" : $"Version {note.Version}",
                    Style   = (Style)Application.Current.Resources["SubHeaderStyle"],
                    Margin  = new Thickness(0, Content.Children.Count == 0 ? 0 : 18, 0, 6),
                };
                Content.Children.Add(heading);

                if (note.Lines.Count == 0)
                {
                    Content.Children.Add(Body("Nothing recorded for this version."));
                    continue;
                }

                foreach (string line in note.Lines)
                    Content.Children.Add(Bullet(line));
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("WhatsNewWindow.Build", ex);
        }
    }

    private static TextBlock Body(string text) => new()
    {
        Text         = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize     = 12.5,
    };

    /// <summary>One entry, with the bullet in its own column so a wrapped line lines up under the
    /// text rather than under the marker.</summary>
    private static Grid Bullet(string text)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var marker = Body("•");
        var body   = Body(text);
        Grid.SetColumn(body, 1);

        row.Children.Add(marker);
        row.Children.Add(body);
        return row;
    }

    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (_placed) return;
        _placed = true;

        try
        {
            AppWindow.MoveAndResize(NativeMethods.CentreRectOnCursorMonitor(WidthDip, MinHeightDip));
            ContentScroller.UpdateLayout();
            FitWindowToContent();
        }
        catch (Exception ex) { AppLog.Error("WhatsNewWindow.MoveAndResize", ex); }
    }

    /// <summary>Sizes the window to the measured content, on the same terms as the About window:
    /// the chrome is exactly what the window and viewport heights differ by.</summary>
    private void FitWindowToContent()
    {
        double viewport = ContentScroller.ViewportHeight;
        if (viewport <= 0 || Content.ActualWidth <= 0) return;

        Content.Measure(new Windows.Foundation.Size(Content.ActualWidth, double.PositiveInfinity));
        double content = Content.DesiredSize.Height
                       + ContentScroller.Padding.Top + ContentScroller.Padding.Bottom;

        double scale  = ((FrameworkElement)ContentScroller).XamlRoot?.RasterizationScale ?? 1.0;
        int heightDip = WindowFit.HeightForContent(AppWindow.Size.Height / scale, content, viewport, MinHeightDip);

        // A long history would otherwise open taller than the monitor; the scroll viewer takes the
        // rest.
        heightDip = Math.Min(heightDip, 720);

        AppWindow.MoveAndResize(NativeMethods.CentreRectOnCursorMonitor(WidthDip, heightDip));
    }
}
