using ChargeKeeper.Services;
using Microsoft.Win32.TaskScheduler;

// Microsoft.Win32.TaskScheduler.Task would otherwise be ambiguous against System.Threading.Tasks.Task.
using ScheduledTask = Microsoft.Win32.TaskScheduler.Task;

namespace ChargeKeeper.Helpers;

/// <summary>
/// Removes the retired product folder once an installation has been moved out of it. The installer
/// does this itself; this is the backstop for the run where a file was still locked. It is also the
/// one moment the old executable is provably not running, because the single-instance mutex is held
/// by the new one.
/// </summary>
internal static class LegacyInstallSweep
{
    /// <summary>
    /// The decision, free of I/O so it stays directly assertable. All three must hold: the process
    /// runs from the current install folder, the retired folder is still on disk, and no scheduled
    /// task still names the executable inside it — removing the folder out from under a task that
    /// starts from it would leave the application unable to start at logon.
    /// </summary>
    internal static bool MayRemove(string? runningExe, bool legacyDirExists, bool aTaskTargetsLegacyExe) =>
        legacyDirExists
        && !aTaskTargetsLegacyExe
        && InstallLocations.IsProductInstallDir(Path.GetDirectoryName(runningExe));

    /// <summary>Runs the sweep for this process. Never throws: losing the sweep costs a stale folder,
    /// and must never cost the application its startup.</summary>
    internal static void TryRun()
    {
        try
        {
            if (Environment.ProcessPath is not { } exe) return;
            if (InstallLocations.LegacySiblingOf(Path.GetDirectoryName(exe)) is not { } legacyDir) return;

            // The common case, and it keeps the two task reads off every start.
            if (!Directory.Exists(legacyDir)) return;

            string legacyExe = Path.Combine(legacyDir, InstallLocations.ExeName);
            if (!MayRemove(exe, legacyDirExists: true, aTaskTargetsLegacyExe: ATaskTargets(legacyExe)))
            {
                AppLog.Info("Legacy install folder kept: a scheduled task still starts from it.");
                return;
            }

            Directory.Delete(legacyDir, recursive: true);
            AppLog.Info($"Legacy install folder removed: {legacyDir}");
        }
        catch (Exception ex) { AppLog.Error("LegacyInstallSweep.TryRun", ex); }
    }

    private static bool ATaskTargets(string exe)
    {
        using var ts = new TaskService();
        return Targets(ts, TaskDefinitions.AutoStartTaskName, exe)
            || Targets(ts, TaskDefinitions.WatchdogTaskName, exe);
    }

    private static bool Targets(TaskService ts, string name, string exe)
    {
        using ScheduledTask? task = ts.GetTask(TaskDefinitions.TaskPath(name));
        return task is not null && TaskDefinitions.TargetsExe(task.Definition, exe);
    }
}
