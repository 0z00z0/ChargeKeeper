using ChargeKeeper.Helpers;
using ChargeKeeper.Services;
using ZeroZero.Lifecycle;

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
        // AppPaths.DataDir is the shared ProductDataPath, which CREATES the folder as it answers, so
        // this is also the only code allowed to name that folder before AppPaths is first touched.
        var reportLegacyMigration = MigrateLegacyAppDataFolder(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

        // Also before the first log line: logging creates Logs\app.log, and an app.log still at the
        // top level could then not be moved onto it.
        var layoutMoves = DataFolderLayout.MoveIntoSubfolders(AppPaths.DataDir);

        var startup = StartupArgs.Parse(Environment.GetCommandLineArgs());

        reportLegacyMigration?.Invoke();
        // A file left in place is found again at every start, and a watchdog probe starts every five
        // minutes, so a probe reports only what moved.
        DataFolderLayout.Report(layoutMoves, includeLeftInPlace: !startup.IsWatchdogProbe);

        // "/debug [on|off]" is a command, not a launch, and must be handled ahead of the
        // single-instance guard: the tray app is normally already running and would win the mutex.
        if (startup.IsDebugCommand)
        {
            CrashDumps.TryHandleDebugCommand(Environment.GetCommandLineArgs(), CrashDumps.DumpDir);
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

        return TryAcquireSingleInstance();
    }

    /// <summary>The process-wide "only one ChargeKeeper" lock — two instances would both claim the
    /// tray icon and write the history CSV with no cross-process locking. The name never changes, or
    /// an old and a new version run side by side during an update.</summary>
    internal const string SingleInstanceMutexName = "Local\\ChargeKeeper.SingleInstance";

    /// <summary>One instant, non-blocking attempt at the lock, held for the life of the process once
    /// taken. An abandoned lock counts as taken: it protects nothing that needs repair.</summary>
    internal static bool TryAcquireSingleInstance() =>
        SingleInstanceLock.Acquire(SingleInstanceMutexName, TimeSpan.Zero).IsTaken();

    /// <summary>Retries <see cref="TryAcquireSingleInstance"/> up to <paramref name="attempts"/>
    /// times, ~200 ms apart, without blocking the calling thread. The wait is silent, invisible dead
    /// time, so see <see cref="StartupArgs.SingleInstanceAttempts"/> for which launches deserve how
    /// many.</summary>
    internal static async Task<bool> TryAcquireSingleInstanceAsync(int attempts)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (TryAcquireSingleInstance()) return true;
            if (attempt < attempts - 1)
                await Task.Delay(200).ConfigureAwait(true);
        }
        return false;
    }

    /// <summary>
    /// One-time migration for the Lenovo Power Tray → ChargeKeeper rename: moves
    /// <c>%AppData%\LenovoPowerTray</c> to <c>%AppData%\ChargeKeeper</c>. Returns the log line to
    /// write, or null when there is nothing to say.
    /// </summary>
    /// <remarks>Returned rather than logged: a log line creates the Logs folder and its app.log, which
    /// has to wait until <see cref="DataFolderLayout"/> has moved the folder's older files. The
    /// destination is composed here rather than asked of <see cref="AppPaths"/>, whose answer creates
    /// the folder and would make the move refuse.</remarks>
    internal static Action? MigrateLegacyAppDataFolder(string appDataRoot)
    {
        try
        {
            var oldDir  = Path.Combine(appDataRoot, "LenovoPowerTray");   // legacy name — kept as-is
            var newDir  = Path.Combine(appDataRoot, AppInfo.Name);
            if (!Directory.Exists(oldDir) || Directory.Exists(newDir)) return null;

            Directory.Move(oldDir, newDir);
            return () => AppLog.Info("Migrated legacy %AppData%\\LenovoPowerTray folder to %AppData%\\ChargeKeeper.");
        }
        catch (Exception ex)
        {
            return () => AppLog.Error("MigrateLegacyAppDataFolder", ex);
        }
    }
}
