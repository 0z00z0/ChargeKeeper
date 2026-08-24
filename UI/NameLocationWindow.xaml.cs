using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using ChargeKeeper.Helpers;

namespace ChargeKeeper.UI;

/// <summary>
/// Name-input prompt for the "Name location" step when adding a network profile for the currently
/// detected network. Small always-on-top popup, centred on the monitor under the cursor.
/// </summary>
internal sealed partial class NameLocationWindow : Window
{
    private readonly TaskCompletionSource<string?> _result = new();

    /// <param name="suggestedName">Pre-filled and pre-selected, so the default can be accepted with one Enter.</param>
    /// <param name="matchKey">The MAC/subnet the profile will match on; the row is hidden when not supplied.</param>
    internal NameLocationWindow(string suggestedName, string? matchKey = null)
    {
        InitializeComponent();

        MatchKeyText.Text = string.IsNullOrWhiteSpace(matchKey) ? "" : $"Matches on {matchKey}";
        MatchKeyText.Visibility = MatchKeyText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        // Must run after the match-key row is set: it measures the content to size the window.
        ConfigureChrome();

        NameBox.Text = suggestedName;
        NameBox.SelectAll();

        OkBtn.Click     += (_, _) => Accept(suggestedName);
        CancelBtn.Click += (_, _) => { _result.TrySetResult(null); Close(); };
        // A dismiss without either button completes as a cancel, rather than leaving the caller's
        // await hanging. TrySetResult, so it is a no-op when a button already completed the result.
        Closed += (_, _) => _result.TrySetResult(null);

        NameBox.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter) Accept(suggestedName);
        };
    }

    private void Accept(string suggestedName)
    {
        string name = string.IsNullOrWhiteSpace(NameBox.Text) ? suggestedName : NameBox.Text.Trim();
        _result.TrySetResult(name);
        Close();
    }

    /// <summary>Shows the window and asynchronously returns the entered name, or null if cancelled.</summary>
    internal async Task<string?> ShowAsync()
    {
        Activate();
        return await _result.Task;
    }

    private void ConfigureChrome()
    {
        WindowChrome.ApplyPopup(this, resizable: false, alwaysOnTop: true);

        Root.Width = 300;
        Root.Measure(new Size(300, double.PositiveInfinity));

        var (work, scale) = NativeMethods.GetCursorMonitorMetrics();
        int cw = (int)Math.Round(300 * scale);
        int ch = (int)Math.Round((Root.DesiredSize.Height > 0 ? Root.DesiredSize.Height : 120) * scale);

        // The CLIENT area must be exactly the measured height, so CentreRectOnCursorMonitor is no use
        // here — it sizes an outer rect. ResizeClient adds the border on top, hence re-reading Size.
        AppWindow.ResizeClient(new SizeInt32(cw, ch));
        var outer = AppWindow.Size;
        var rect  = NativeMethods.CentreInWorkArea(work, outer.Width, outer.Height);
        AppWindow.Move(new PointInt32(rect.X, rect.Y));
    }
}
