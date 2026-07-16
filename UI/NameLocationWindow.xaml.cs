using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using ChargeKeeper.Helpers;

namespace ChargeKeeper.UI;

/// <summary>
/// Minimal name-input prompt (TODO #31) — the "Name location" step when adding a network-profile
/// configuration for the currently-detected network. Small always-on-top, bordered, no-title-bar
/// chrome (see <see cref="ConfigureChrome"/>), centred on the monitor under the cursor — the same
/// popup style the app's other small dialogs use, just with a text-input step instead of static
/// content.
/// </summary>
internal sealed partial class NameLocationWindow : Window
{
    private readonly TaskCompletionSource<string?> _result = new();

    /// <param name="suggestedName">
    /// Pre-filled and pre-selected so accepting the default is a single Enter/click — derived from
    /// the detected location's WiFi SSID or adapter name (see <c>NetworkLocationService.DisplayHint</c>),
    /// never left blank, so there's always something sensible to accept without typing.
    /// </param>
    internal NameLocationWindow(string suggestedName)
    {
        InitializeComponent();
        ConfigureChrome();

        NameBox.Text = suggestedName;
        NameBox.SelectAll();

        OkBtn.Click     += (_, _) => Accept(suggestedName);
        CancelBtn.Click += (_, _) => { _result.TrySetResult(null); Close(); };
        // Alt+F4 / clicking away (this window is always-on-top but not focus-locked) without
        // using either button — treat exactly like Cancel rather than leaving the caller's await
        // hanging forever. TrySetResult (not SetResult) makes this a safe no-op when a button
        // already completed the result.
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

        // Size first, then centre: this window's height is whatever the measured content needs, and
        // the CLIENT area is what must be exactly that — so it can't use
        // NativeMethods.CenterRectOnCursorMonitor (one MoveAndResize of an outer rect, which would
        // shrink the client by the border). ResizeClient adds the non-client border on top, hence
        // re-reading AppWindow.Size for the outer extent the centring must actually work from.
        AppWindow.ResizeClient(new SizeInt32(cw, ch));
        var outer = AppWindow.Size;
        var rect  = NativeMethods.CenterInWorkArea(work, outer.Width, outer.Height);
        AppWindow.Move(new PointInt32(rect.X, rect.Y));
    }
}
