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
    /// Places the window once, on first activation: centred on the monitor under the cursor — the
    /// one the user just used the tray menu on — and sized for THAT monitor's scaling. Guarded: a
    /// placement failure must never stop the window from showing.
    /// </summary>
    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (_placed) return;
        _placed = true;

        try { AppWindow.MoveAndResize(NativeMethods.CenterRectOnCursorMonitor(WidthDip, HeightDip)); }
        catch (Exception ex) { AppLog.Error("AboutWindow.MoveAndResize", ex); }
    }
}
