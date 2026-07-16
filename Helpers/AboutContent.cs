using ZeroZero.Brand.Core;

namespace ChargeKeeper.Helpers;

/// <summary>
/// The About payload, in one place because TWO surfaces now render it: the standalone
/// <see cref="UI.AboutWindow"/> (opened from the tray "About…" item) and the About section
/// embedded in the Settings window. Both hand this to the shared
/// <c>ZeroZero.Brand.WinUI.BrandAboutControl</c>, so neither can drift from the other on
/// wording, version or credits — the same anti-drift reason <see cref="AppInfo"/> exists.
///
/// <para>Keep <see cref="Build"/>'s external-libraries list in sync with the README's
/// "External libraries" table (the same non-Microsoft NuGet dependencies): the studio rule is
/// that every third-party library is credited with its author and licence.</para>
/// </summary>
internal static class AboutContent
{
    /// <summary>Builds the About payload. Pure data — no I/O, cannot throw in practice.</summary>
    internal static AboutInfo Build() => new()
    {
        AppName     = AppInfo.Name,
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
            new ExternalLibrary("MQTTnet", "The MQTTnet Project", "MQTT client for the Home Assistant integration", "MIT", "https://github.com/dotnet/MQTTnet"),
        ],
    };
}
