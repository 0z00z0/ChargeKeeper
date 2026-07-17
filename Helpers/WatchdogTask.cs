using ChargeKeeper.Services;
using Microsoft.Win32.TaskScheduler;

// Microsoft.Win32.TaskScheduler.Task would otherwise be ambiguous against System.Threading.Tasks.Task
// (ImplicitUsings), and "Task" alone reads like the async one at every use site here.
using ScheduledTask = Microsoft.Win32.TaskScheduler.Task;

namespace ChargeKeeper.Helpers;

/// <summary>
/// Keeps the tray app alive via Task Scheduler, and keeps Task Scheduler from being the thing
/// that kills it. <see cref="TaskDefinitions"/> owns what the tasks look like; this class owns
/// when they get written and what gets logged.
///
/// Two tasks are maintained at startup (best-effort, elevated app so no UAC):
///  1. "ChargeKeeper AutoStart" — repaired in place (power-safe settings) when it exists and
///     points at THIS exe; never created here (running at startup stays the user's choice —
///     <see cref="TaskSchedulerHelper.SetAutoStart"/> is the only creator).
///  2. "ChargeKeeper Watchdog"  — created/refreshed unconditionally: it is entirely ours.
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

    /// <summary>
    /// Registers the watchdog task and repairs the AutoStart task. Never throws. Skipped
    /// entirely for non-installed runs (dev builds under bin\...) — a watchdog or startup task
    /// pointing at a build-output exe would resurrect stale dev binaries for weeks.
    /// </summary>
    internal static void TryEnsureTasks()
    {
        try
        {
            if (Environment.ProcessPath is not { } exe) return;
            // Two accepted install locations: fresh installs live in "...\ChargeKeeper";
            // installs upgraded across the Lenovo Power Tray -> ChargeKeeper rename keep the
            // old folder name (the installer reuses the AppId-recorded {app} directory).
            if (!exe.EndsWith(@"\ChargeKeeper\ChargeKeeper.exe", StringComparison.OrdinalIgnoreCase) &&
                !exe.EndsWith(@"\Lenovo Power Tray\ChargeKeeper.exe", StringComparison.OrdinalIgnoreCase))
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

        // Not disposed: Task caches this instance and hands back the same one on every read, so it
        // belongs to `existing` and dies with it.
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

    /// <summary>
    /// This class's error policy around the shared <see cref="TaskDefinitions.Register"/>: best-effort,
    /// because a failure here costs the safety net and must never cost the app its startup. The tray
    /// toggle takes the opposite stance on the very same call — see TaskSchedulerHelper.SetAutoStart.
    /// </summary>
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
