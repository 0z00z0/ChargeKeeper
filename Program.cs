using ChargeKeeper.Helpers;
using ChargeKeeper.Services;

namespace ChargeKeeper;

/// <summary>
/// The process entry point. Replaces the XAML-generated one (DISABLE_XAML_GENERATED_MAIN in the
/// csproj); the generated version is otherwise identical to the fall-through at the bottom of
/// <see cref="Main"/>.
///
/// <para>It exists because two of this exe's invocations are NOT app launches, and the generated
/// Main gave them no way to say so. <c>Application.Start</c> runs the whole Windows App SDK
/// bootstrap, then the <see cref="App"/> constructor, then InitializeComponent() — which parses
/// App.xaml and its XamlControlsResources dictionary — before a single line of ours can decide the
/// process should not be here. So the checks that used to sit at the top of
/// <see cref="App.OnLaunched"/> ran only AFTER paying for a full WinUI stack out of a 234 MB / 302
/// DLL install. The watchdog task (see <see cref="WatchdogTask"/>) probes every 5 minutes plus on
/// session-unlock and resume — ~288 elevated boots a day, landing precisely at unlock and resume,
/// i.e. exactly when the machine is busiest and the user is watching. Deciding here costs a bare
/// CLR start and nothing else.</para>
///
/// <para>The steady state for BOTH short-circuit paths is "exit immediately": the app is normally
/// already running (so every probe turns around) and <c>/debug</c> is a one-shot command. Only a
/// genuine resurrection — or a real launch — falls through to <c>Application.Start</c>.</para>
///
/// <para>A short-circuited process never constructs <see cref="App"/>, so it registers no
/// ProcessExit handler: these exits cannot trip the self-heal relaunch and leave no trace in
/// app.log. That is structural now, and replaces the "_quietExit" flag that used to suppress the
/// 288 duplicate-instance/ProcessExit line pairs a day that would otherwise bury the real
/// forensics.</para>
///
/// <para>One thing does still run ahead of this: the Windows App SDK's [ModuleInitializer]
/// (MddBootstrapAutoInitializer.cs, pulled in by the SDK targets). In the SHIPPED configuration
/// that is nearly free — the installer publishes --self-contained, which drops
/// MICROSOFT_WINDOWSAPPSDK_AUTOINITIALIZE_BOOTSTRAP and leaves only the undocked reg-free WinRT
/// hook, so no runtime package is resolved or loaded before Main. A framework-dependent local
/// `dotnet build` DOES still pay MddBootstrap.TryInitialize on every probe; that is a dev-build
/// property, not something this entry point can (or needs to) prevent. Verify with
/// `dotnet build -p:SelfContained=true -getProperty:DefineConstants` before concluding a probe is
/// slower than it looks.</para>
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Must run before ANYTHING touches %AppData%\ChargeKeeper — an AppLog write, a marker, a
        // settings read — because Directory.Move refuses to move onto an existing destination, and a
        // half-created new folder would strand the user's settings + battery history in the old one
        // forever. This is now the app's single first-thing-that-runs on every path (it used to sit
        // at the top of the App constructor, which the two command paths below no longer reach).
        MigrateLegacyAppDataFolder();

        var startup = StartupArgs.Parse(Environment.GetCommandLineArgs());

        // "/debug [on|off]" is a COMMAND, not a launch: it records whether crash-dump capture should
        // be armed, applies that to the registry, and exits without ever showing a tray icon. It must
        // be handled ahead of the single-instance guard — the tray app already running is this app's
        // steady state, i.e. precisely the situation in which the user types this, so a /debug process
        // that had to win the mutex would exit before doing anything. No elevation dance needed: the
        // manifest is requireAdministrator, so this process is already elevated for the HKLM write.
        // CrashDumps owns the rest of the reasoning.
        if (startup.IsDebugCommand)
        {
            CrashDumps.TryHandleDebugCommand(Environment.GetCommandLineArgs(), AppPaths.DataFile("dumps"));
            return;
        }

        // A watchdog probe answers one question — "is the tray app gone and wanted back?" — and the
        // answer is almost always no. Both inputs are cheap (a File.Exists and one non-blocking mutex
        // acquire), so the probe resolves and exits here, without WinUI ever loading.
        if (startup.IsWatchdogProbe && !WatchdogProbeShouldResurrect())
            return;

        // Fall-through: a real launch, or a probe that found the app genuinely gone. Verbatim the
        // XAML-generated Main from here on.
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(p =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            // Application.Start owns the instance from here (Application.Current) — nothing to hold.
            _ = new App(startup);
        });
    }

    /// <summary>
    /// The watchdog probe's whole decision. Returns false — exit now, leave things as they are —
    /// unless the tray app is really gone AND the user has not deliberately stopped it.
    ///
    /// <para>On true, this process now OWNS the single-instance lock (that is what makes the "is it
    /// alive?" check meaningful in the first place: claiming the mutex is the only way to know
    /// nobody else holds it). It is deliberately NOT released before <c>Application.Start</c> —
    /// releasing it would open a window for a second instance to slip in, and re-acquiring it later
    /// could fail. <see cref="App.OnLaunched"/> sees <see cref="SingleInstance.IsHeld"/> and skips
    /// its own acquire accordingly.</para>
    ///
    /// <para>A single instant attempt, no retry: the 3 s retry in
    /// <see cref="SingleInstance.TryAcquireAsync"/> exists for the self-heal relaunch race, and a
    /// probe finding a live instance is the expected steady state, not a race worth waiting out.</para>
    /// </summary>
    private static bool WatchdogProbeShouldResurrect()
    {
        // A deliberate tray-menu Exit outranks the watchdog: the hold marker written by App.Shutdown
        // keeps probes from resurrecting an app the user chose to stop. Checked first — it is the one
        // input that says "no" even when the app really is gone.
        if (WatchdogTask.HoldMarkerExists) return false;

        return SingleInstance.TryAcquire();
    }

    /// <summary>
    /// One-time migration for the rename from Lenovo Power Tray to ChargeKeeper: moves the old
    /// <c>%AppData%\LenovoPowerTray</c> folder to <c>%AppData%\ChargeKeeper</c> so settings,
    /// battery history, and logs survive the upgrade. Runs only when the old folder exists and the
    /// new one doesn't (i.e. exactly once); a failure is logged and never crashes startup — the
    /// app then simply starts with fresh defaults, same as a clean install.
    /// </summary>
    private static void MigrateLegacyAppDataFolder()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var oldDir  = Path.Combine(appData, "LenovoPowerTray");   // legacy name — kept as-is
            var newDir  = AppPaths.DataDir;
            if (!Directory.Exists(oldDir) || Directory.Exists(newDir)) return;

            Directory.Move(oldDir, newDir);
            AppLog.Info("Migrated legacy %AppData%\\LenovoPowerTray folder to %AppData%\\ChargeKeeper.");
        }
        catch (Exception ex)
        {
            // Logged only AFTER the move attempt — AppLog itself creates the new folder.
            AppLog.Error("MigrateLegacyAppDataFolder", ex);
        }
    }
}
