using ZeroZero.Brand.Core;

namespace ChargeKeeper.Helpers;

/// <summary>
/// The About payload, shared by the standalone <see cref="UI.AboutWindow"/> and the About section
/// embedded in the Settings window, so neither can drift on wording, version or credits.
/// <para><see cref="Build"/>'s external-libraries list must match the README's "External libraries"
/// table: <c>AboutCreditsTests</c> parses that table and asserts row-for-row equality, so editing
/// one side alone fails the build.</para>
/// </summary>
internal static class AboutContent
{
    /// <summary>Width in DIPs at which <c>BrandAboutControl</c> lays out correctly — narrow enough
    /// that prose wraps at a readable measure, wide enough that a library credit row does not wrap
    /// mid-row. A property of the shared control, so both hosts must use this one number.</summary>
    internal const int ContentWidthDip = 460;

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
            new ExternalLibrary("MQTTnet", "The MQTTnet Project", "MQTT client for the broker integration", "MIT", "https://github.com/dotnet/MQTTnet"),
            new ExternalLibrary("NLog", "Jarek Kowalski, Kim Christensen, Julian Verdurmen", "Event log with size/age-based rotation (app.log)", "BSD-3-Clause", "https://github.com/NLog/NLog"),
        ],
    };
}
