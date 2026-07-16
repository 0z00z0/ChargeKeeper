using System;
using Microsoft.UI.Xaml;
using Windows.Graphics;
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
    // Target size in DIPs; scaled to the physical pixels of whichever monitor it lands on.
    private const int WidthDip  = 460;
    private const int HeightDip = 660;

    private bool _placed;

    public AboutWindow()
    {
        InitializeComponent();
        Title = "About ChargeKeeper";

        // Dark-theme the standard title bar so it matches the Mica BaseAlt backdrop.
        ChargeKeeper.Helpers.TitleBarTheme.ApplyDark(AppWindow);

        About.SetInfo(AboutContent.Build());

        // The libraries expander changes the content height; the ScrollViewer already handles
        // overflow, so nothing more is required here — but keep the window from being smaller than
        // the collapsed content on first show.
        Activated += OnActivated;
    }

    /// <summary>
    /// Places the window once, on first activation: centred on the monitor under the cursor —
    /// the one the user just used the tray menu on — and sized for THAT monitor's scaling, so it
    /// is never half-off a screen or mis-sized on a mixed-DPI setup.
    ///
    /// <para>Uses the native <see cref="NativeMethods.GetCursorMonitorMetrics"/> path rather than
    /// <c>DisplayArea.FindAll</c> for the same reason
    /// <see cref="SettingsWindow"/>'s placement does — the latter faulted on a multi-monitor setup.
    /// Scale comes from the cursor's monitor, not <c>XamlRoot.RasterizationScale</c> (the monitor
    /// the window happens to have opened on): the window is about to be MOVED to the cursor's
    /// monitor, so position and size must be computed against the same one. Guarded — a placement
    /// failure must never stop the window from showing.</para>
    /// </summary>
    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (_placed) return;
        _placed = true;

        try
        {
            var (work, scale) = NativeMethods.GetCursorMonitorMetrics();
            int workW = work.Right  - work.Left;
            int workH = work.Bottom - work.Top;
            int w = Math.Min((int)Math.Round(WidthDip  * scale), workW);
            int h = Math.Min((int)Math.Round(HeightDip * scale), workH);
            AppWindow.MoveAndResize(new RectInt32(
                work.Left + (workW - w) / 2,
                work.Top  + (workH - h) / 2,
                w, h));
        }
        catch (Exception ex) { AppLog.Error("AboutWindow.MoveAndResize", ex); }
    }
}
