using System;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using ChargeKeeper.Helpers;
using ZeroZero.Brand.Core;

namespace ChargeKeeper.UI;

/// <summary>
/// #59: ChargeKeeper's own About window. Owns only the window chrome (Mica BaseAlt backdrop,
/// sizing, close) and hosts the shared <see cref="ZeroZero.Brand.WinUI.BrandAboutControl"/> for
/// the actual content — brand header, description, repo/website/donate links and the external-
/// libraries credit list. The shared control deliberately has no "Check for updates" button;
/// that stays on the tray menu (<c>TrayMenu.CheckForUpdatesAsync</c>).
///
/// <para>Single reusable instance owned by <see cref="TrayMenu"/> (its <c>_aboutWindow</c>
/// field), so both the tray "About…" item and the Settings "About ChargeKeeper" button reuse
/// one window.</para>
/// </summary>
internal sealed partial class AboutWindow : Window
{
    private const string AppName = AppInfo.Name;
    private bool _sized;

    public AboutWindow()
    {
        InitializeComponent();
        Title = "About ChargeKeeper";

        // Dark-theme the standard title bar so it matches the Mica BaseAlt backdrop.
        ChargeKeeper.Helpers.TitleBarTheme.ApplyDark(AppWindow);

        About.SetInfo(BuildInfo());

        // The libraries expander changes the content height; the ScrollViewer already handles
        // overflow, so nothing more is required here — but keep the window from being smaller than
        // the collapsed content on first show.
        Activated += OnActivated;
    }

    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (_sized) return;
        _sized = true;

        // Size once, DPI-correct: XamlRoot is available by first activation, so scale the DIP
        // target by the monitor's rasterisation scale (AppWindow.Resize takes physical pixels).
        double scale = Content?.XamlRoot?.RasterizationScale ?? 1.0;
        AppWindow.Resize(new SizeInt32(
            (int)Math.Round(460 * scale),
            (int)Math.Round(660 * scale)));
    }

    /// <summary>
    /// The About payload — same data the tray menu used to build inline. Kept here so the one
    /// window that renders it also owns it. Keep the external-libraries list in sync with the
    /// README's "External libraries" table (same non-Microsoft NuGet dependencies).
    /// </summary>
    private static AboutInfo BuildInfo() => new()
    {
        AppName     = AppName,
        Version     = AppInfo.Version,
        Description = "Keeps your laptop battery healthy — charge limits, a live battery gauge and smart standby control from the system tray. Runs on ThinkPads today (requires the Lenovo Power Management Driver).",
        RepoUrl     = "https://github.com/0z00z0/ChargeKeeper",
        ExternalLibraries =
        [
            new ExternalLibrary("H.NotifyIcon.WinUI", "HavenDV", "System-tray icon + native context menu for WinUI 3", "MIT", "https://github.com/HavenDV/H.NotifyIcon"),
            new ExternalLibrary("TaskScheduler", "David Hall", "Managed wrapper over the Windows Task Scheduler API (auto-start)", "MIT", "https://github.com/dahall/TaskScheduler"),
            new ExternalLibrary("CommunityToolkit.WinUI.Controls.RangeSelector", ".NET Foundation", "Dual-handle range slider (Smart Charge start/stop threshold)", "MIT", "https://github.com/CommunityToolkit/Windows"),
            new ExternalLibrary("CommunityToolkit.WinUI.Controls.SettingsControls", ".NET Foundation", "SettingsCard/SettingsExpander rows (Settings window)", "MIT", "https://github.com/CommunityToolkit/Windows"),
            new ExternalLibrary("WinUIEx", "Morten Nielsen", "WinUI 3 window helper extensions (Settings window placement)", "MIT", "https://github.com/dotMorten/WinUIEx"),
            new ExternalLibrary("MQTTnet", "The MQTTnet Project", "MQTT client for the MQTT publishing integration", "MIT", "https://github.com/dotnet/MQTTnet"),
        ],
    };
}
