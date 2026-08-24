using ChargeKeeper.Helpers;
using ChargeKeeper.Services;

namespace ChargeKeeper;

/// <summary>
/// The process entry point, replacing the XAML-generated one (DISABLE_XAML_GENERATED_MAIN in the
/// csproj). It exists so a watchdog probe and the <c>/debug</c> command — neither of which is an app
/// launch — can decide the process should not be here before <c>Application.Start</c> boots WinUI.
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Must run before ANYTHING touches %AppData%\ChargeKeeper — a log write, a marker, a settings
        // read — because Directory.Move refuses an existing destination, and a half-created new
        // folder would strand the user's settings and battery history in the old one forever.
        MigrateLegacyAppDataFolder();

        var startup = StartupArgs.Parse(Environment.GetCommandLineArgs());

        // "/debug [on|off]" is a command, not a launch, and must be handled ahead of the
        // single-instance guard: the tray app is normally already running and would win the mutex.
        if (startup.IsDebugCommand)
        {
            CrashDumps.TryHandleDebugCommand(Environment.GetCommandLineArgs(), AppPaths.DataFile("dumps"));
            return;
        }

        if (startup.IsWatchdogProbe && !WatchdogProbeShouldResurrect())
            return;

        // Fall-through — a real launch, or a probe that found the app gone. Verbatim the generated Main.
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
    /// The watchdog probe's whole decision: true only when the tray app is really gone AND the user
    /// has not deliberately stopped it. On true this process holds the single-instance lock and
    /// deliberately keeps it, so <see cref="App.OnLaunched"/> skips its own acquire.
    /// </summary>
    private static bool WatchdogProbeShouldResurrect()
    {
        // A deliberate tray-menu Exit outranks the watchdog. Checked first — it is the one input
        // that says "no" even when the app really is gone.
        if (WatchdogTask.HoldMarkerExists) return false;

        return SingleInstance.TryAcquire();
    }

    /// <summary>
    /// One-time migration for the Lenovo Power Tray → ChargeKeeper rename: moves
    /// <c>%AppData%\LenovoPowerTray</c> to <c>%AppData%\ChargeKeeper</c>, logging any failure.
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
