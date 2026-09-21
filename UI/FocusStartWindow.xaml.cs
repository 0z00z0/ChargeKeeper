using ChargeKeeper.Helpers;
using ChargeKeeper.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace ChargeKeeper.UI;

/// <summary>
/// The dashboard's start box: the length of the next session, what it will do, and one button that
/// starts it.
/// </summary>
/// <remarks>
/// <para>It starts a session and nothing else. There is no control here that ends one, and there is
/// none anywhere else on the machine — ending is Home Assistant's, after the five-minute wait and
/// the second request.</para>
/// <para>The length is the one thing this box decides; the three levers are shown as they stand,
/// chosen either from Home Assistant or from the Settings focus page, which the settings button
/// opens and where the length can also be set.</para>
/// </remarks>
internal sealed partial class FocusStartWindow : Window
{
    /// <summary>Narrow enough for the shortest work area this runs on, and the same width the
    /// dashboard itself uses.</summary>
    private const int WidthDip = 340;

    private const int MinHeightDip = 280;

    /// <summary>The lengths offered. The same list the Settings focus page carries, because the two
    /// set the same value.</summary>
    private static readonly (string Label, int Value)[] Lengths =
    [
        ("15 min", 15), ("25 min", 25), ("45 min", 45), ("1 hour", 60),
        ("90 min", 90), ("2 hours", 120), ("3 hours", 180), ("4 hours", 240),
    ];

    private readonly App _app;
    private readonly Action _afterStart;
    private readonly PopupWindowFit _fit;

    private bool _placed;
    private bool _closing;

    internal FocusStartWindow(App app, Action afterStart)
    {
        InitializeComponent();
        Title = "Start a focus session";

        _app        = app;
        _afterStart = afterStart;

        WindowChrome.ApplyPopup(this, resizable: false, alwaysOnTop: true);
        AppTitleBar.Apply(this);

        Build();

        _fit = new PopupWindowFit(this, Content, ContentScroller.Padding, WidthDip, MinHeightDip,
                                  "FocusStartWindow");

        Activated += OnActivated;
        Content.SizeChanged += (_, _) => { if (_placed && !_closing) _fit.FitToContent(); };
        Closed += (_, _) => _closing = true;
    }

    private void Build()
    {
        var s = SettingsService.Current;

        foreach (var (label, value) in Lengths)
            MinutesCombo.Items.Add(new ComboBoxItem { Content = label, Tag = value });

        // A stored length that is not one of the offered ones stays what it is rather than being
        // silently rounded to a neighbour.
        if (!Lengths.Any(l => l.Value == s.FocusSessionMinutes))
            MinutesCombo.Items.Insert(0, new ComboBoxItem
            {
                Content = $"{s.FocusSessionMinutes} min",
                Tag     = s.FocusSessionMinutes,
            });

        MinutesCombo.SelectedItem = MinutesCombo.Items.Cast<ComboBoxItem>()
            .First(i => (int)i.Tag! == s.FocusSessionMinutes);

        LeverRow("Block the network", s.FocusBlocksNetwork);
        LeverRow("Dim the screen", s.FocusDimsScreen);
        LeverRow("Cover every screen", s.FocusCoversScreen);
    }

    /// <summary>One lever, shown and not offered: a tick or a dash and the lever's name.</summary>
    private void LeverRow(string name, bool on)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new TextBlock
        {
            Text       = on ? "✓" : "—",
            FontSize   = 12,
            MinWidth   = 14,
            Foreground = on ? AppColors.StatusChargingBrush : AppColors.StatusUnknownBrush,
        });
        row.Children.Add(new TextBlock
        {
            Text       = name,
            FontSize   = 12,
            Foreground = on
                ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorPrimaryBrush"]
                : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        LeverRows.Children.Add(row);
    }

    /// <summary>The length chosen in the box, or null when the selection cannot be read — in which
    /// case the stored default stands.</summary>
    private int? ChosenMinutes() =>
        MinutesCombo.SelectedItem is ComboBoxItem { Tag: int minutes } ? minutes : null;

    private void OnStartButton(object sender, RoutedEventArgs e)
    {
        int? chosen = ChosenMinutes();

        // Written back first, so the length shown here, the one on the Settings page and the one
        // Home Assistant publishes are one value rather than three.
        if (chosen is { } minutes) SettingsService.Update(s => s.FocusSessionMinutes = minutes);

        var outcome = FocusSessionService.Arm("the dashboard", chosen);
        if (outcome == FocusArmOutcome.Armed)
        {
            _afterStart();
            Dismiss();
            return;
        }

        // Nothing was armed. The box stays open saying why, rather than closing on a session that
        // never started.
        RefusalText.Text = Refusal(outcome);
        RefusalText.Visibility = Visibility.Visible;
        _fit.FitToContent();
    }

    private static string Refusal(FocusArmOutcome outcome) => outcome switch
    {
        FocusArmOutcome.AlreadyRunning => "A session is already running.",
        FocusArmOutcome.NoLeverChosen  => "No lever is switched on, so the session would do nothing "
                                        + "but count down. Switch one on in Home Assistant first.",
        FocusArmOutcome.LeverRefused   => "One of the levers cannot be used right now — the MQTT "
                                        + "broker port may be set to Automatic, or no display "
                                        + "accepts a brightness. The log says which.",
        _                              => "A lever failed to engage, and whatever did engage is back "
                                        + "as it was. The log says which.",
    };

    private void OnFocusSettingsButton(object sender, RoutedEventArgs e)
    {
        _app.ShowSettingsWindowOnPage("Focus");
        Dismiss();
    }

    private void OnCancelButton(object sender, RoutedEventArgs e) => Dismiss();

    private void OnEscapeInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Dismiss();
    }

    /// <summary>Closes once. Opening Settings or starting a session also takes the focus away, so
    /// the close-on-deactivate below would otherwise close a window already closing.</summary>
    private void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        try { Close(); }
        catch (Exception ex) { AppLog.Error("FocusStartWindow.Dismiss", ex); }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        // Clicking away dismisses it, as it does on the dashboard this was opened from.
        if (e.WindowActivationState == WindowActivationState.Deactivated)
        {
            if (WindowChrome.DismissalHeld) return;
            Dismiss();
            return;
        }

        if (_placed) return;
        _placed = true;

        try
        {
            AppWindow.MoveAndResize(NativeMethods.CentreRectOnCursorMonitor(WidthDip, MinHeightDip));
            ContentScroller.UpdateLayout();
            _fit.FitToContent();
        }
        catch (Exception ex) { AppLog.Error("FocusStartWindow.MoveAndResize", ex); }
    }
}
