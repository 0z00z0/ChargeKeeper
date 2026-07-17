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
/// that every third-party library is credited with its author and licence. This is enforced, not
/// merely requested — <c>AboutCreditsTests</c> parses that table and asserts row-for-row equality
/// (name, author, purpose, licence), so editing one side alone fails the build. It drifted once
/// while this paragraph was the only guard.</para>
/// </summary>
internal static class AboutContent
{
    /// <summary>
    /// Width in DIPs at which <c>BrandAboutControl</c>'s content lays out correctly — narrow enough
    /// that the description and the credit lines wrap at a readable measure, wide enough that a
    /// library row's "name — author — licence" doesn't wrap mid-row.
    ///
    /// <para>A property of the SHARED control, not of either host, so both hosts must use this one
    /// number: <see cref="UI.AboutWindow"/> as its window width, and the Settings About panel as its
    /// card MaxWidth (applied in code — the XAML deliberately hard-codes no width). Two hard-coded
    /// 460s previously encoded this same fact and would have drifted the moment one was widened,
    /// which is the exact failure <see cref="Build"/> exists to prevent, one level up in the chrome.</para>
    /// </summary>
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
            new ExternalLibrary("MQTTnet", "The MQTTnet Project", "MQTT client for the Home Assistant integration", "MIT", "https://github.com/dotnet/MQTTnet"),
            new ExternalLibrary("NLog", "Jarek Kowalski, Kim Christensen, Julian Verdurmen", "Event log with size/age-based rotation (app.log)", "BSD-3-Clause", "https://github.com/NLog/NLog"),
        ],
    };
}
