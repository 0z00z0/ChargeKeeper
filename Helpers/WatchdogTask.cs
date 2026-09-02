using ChargeKeeper.Services;
using Microsoft.Win32.TaskScheduler;

// Microsoft.Win32.TaskScheduler.Task would otherwise be ambiguous against System.Threading.Tasks.Task
// (ImplicitUsings), and "Task" alone reads like the async one at every use site here.
using ScheduledTask = Microsoft.Win32.TaskScheduler.Task;

namespace ChargeKeeper.Helpers;

/// <summary>
/// Keeps the tray app alive via Task Scheduler, and keeps Task Scheduler from being the thing that
/// kills it. <see cref="TaskDefinitions"/> owns what the tasks look like; this class owns when they
/// get written and what gets logged. AutoStart is only ever repaired here, never created — that
/// stays the user's choice via <see cref="TaskSchedulerHelper.SetAutoStart"/> — while the Watchdog
/// task is entirely ours and is refreshed unconditionally.
/// </summary>
internal static class WatchdogTask
{
    private static string HoldMarkerPath => AppPaths.DataFile("watchdog-hold.marker");

    internal static bool HoldMarkerExists => File.Exists(HoldMarkerPath);

    /// <summary>Written on tray-menu Exit so watchdog probes leave a deliberate exit alone.</summary>
    internal static void WriteHoldMarker()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HoldMarkerPath)!);
            File.WriteAllText(HoldMarkerPath, DateTimeOffset.Now.ToString("O"));
        }
        catch (Exception ex) { AppLog.Error("WatchdogTask.WriteHoldMarker", ex); }
    }

    /// <summary>Cleared on every deliberate start, so resurrection is re-armed.</summary>
    internal static void TryClearHoldMarker()
    {
        try { File.Delete(HoldMarkerPath); }
        catch { /* best-effort */ }
    }

    /// <summary>Registers the watchdog task and repairs the AutoStart task. Never throws. Skipped for
    /// non-installed runs: a task pointing at a build-output exe would resurrect stale dev binaries
    /// for weeks.</summary>
    internal static void TryEnsureTasks()
    {
        try
        {
            if (Environment.ProcessPath is not { } exe) return;
            // Two accepted install locations: an installation the installer has not yet moved still
            // runs from the retired folder name, and must keep both its tasks maintained.
            if (!InstallLocations.IsInstalledExe(exe))
            {
                AppLog.Info("Watchdog: not running from the install directory — task registration skipped.");
                return;
            }

            if (TaskIdentity.Current() is not { } user) return;

            using var ts = new TaskService();
            EnsureWatchdogTask(ts, exe, user);
            RepairAutoStartTask(ts, exe, user);
        }
        catch (Exception ex) { AppLog.Error("WatchdogTask.TryEnsureTasks", ex); }
    }

    private static void EnsureWatchdogTask(TaskService ts, string exe, TaskIdentity user)
    {
        using ScheduledTask? existing = ts.GetTask(TaskDefinitions.TaskPath(TaskDefinitions.WatchdogTaskName));
        if (existing is not null && TaskDefinitions.Matches(existing.Definition, exe))
            return;   // current definition already registered

        using TaskDefinition td = TaskDefinitions.BuildWatchdog(ts, exe, user);
        bool ok = Register(ts, TaskDefinitions.WatchdogTaskName, td);
        AppLog.Info(ok
            ? $"Watchdog: scheduled task '{TaskDefinitions.WatchdogTaskName}' registered (5-min + unlock + resume probes)."
            : $"Watchdog: FAILED to register '{TaskDefinitions.WatchdogTaskName}' — no external restart safety net this run.");
    }

    private static void RepairAutoStartTask(TaskService ts, string exe, TaskIdentity user)
    {
        using ScheduledTask? existing = ts.GetTask(TaskDefinitions.TaskPath(TaskDefinitions.AutoStartTaskName));
        if (existing is null) return;                                     // no task — user opted out of autostart

        // Not disposed: Task caches this instance, so it belongs to `existing` and dies with it.
        TaskDefinition current = existing.Definition;
        if (TaskDefinitions.Matches(current, exe)) return;                // already correct
        if (!TaskDefinitions.TargetsExe(current, exe))
        {
            // Task points at some other exe (e.g. an old install path) — leave it alone rather
            // than hijack it; the installer owns that transition.
            AppLog.Info("Watchdog: AutoStart task points at a different exe — repair skipped.");
            return;
        }

        using TaskDefinition repaired = TaskDefinitions.BuildAutoStart(ts, exe, user);
        bool ok = Register(ts, TaskDefinitions.AutoStartTaskName, repaired);
        AppLog.Info(ok
            ? "Watchdog: AutoStart task repaired — StopIfGoingOnBatteries and the 72h execution "
              + "limit removed (Task Scheduler defaults; they hard-killed the app at undock)."
            : "Watchdog: FAILED to repair the AutoStart task — undock may still kill task-started instances.");
    }

    /// <summary>Best-effort wrapper around <see cref="TaskDefinitions.Register"/>: a failure here
    /// costs the safety net, and must never cost the app its startup. The tray toggle takes the
    /// opposite stance on the same call.</summary>
    private static bool Register(TaskService ts, string name, TaskDefinition definition)
    {
        try
        {
            TaskDefinitions.Register(ts, name, definition);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error($"WatchdogTask.Register({name})", ex);
            return false;
        }
    }
}
