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

    // Latched on Closed, so a size-change retry arriving as the window tears down does nothing.
    private bool _closing;

    private readonly PopupWindowFit _fit;

    public WhatsNewWindow()
    {
        InitializeComponent();
        Title = "What's new in ChargeKeeper";

        AppTitleBar.Apply(this);
        Build();

        _fit = new PopupWindowFit(this, Content, ContentScroller.Padding, WidthDip, MinHeightDip,
                                  "WhatsNewWindow");

        // Placed on first activation. The content is often not laid out yet at that moment, so the
        // fit is retried when the panel first takes its real height — same defect and fix as
        // AboutWindow (#225).
        Activated += OnActivated;
        Content.SizeChanged += (_, _) => { if (_placed && !_closing) _fit.FitToContent(); };
        Closed += (_, _) => _closing = true;
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
            _fit.FitToContent();
        }
        catch (Exception ex) { AppLog.Error("WhatsNewWindow.MoveAndResize", ex); }
    }
}
