using Microsoft.Win32.TaskScheduler;

namespace ChargeKeeper.Helpers;

/// <summary>
/// The user's auto-start toggle: creates and removes the logon task behind the tray menu's "Start
/// with Windows". A scheduled task rather than a Run key, because a Run-key entry for an elevated
/// app triggers a UAC prompt on every boot. The definition comes from <see cref="TaskDefinitions"/>,
/// so this writer and <see cref="WatchdogTask"/>'s startup repair cannot disagree about it.
/// </summary>
internal static class TaskSchedulerHelper
{
    /// <summary>Composed by <see cref="TaskDefinitions.TaskPath"/> rather than here, so this reader
    /// cannot disagree with the writers about where the task lives.</summary>
    private static string TaskPath => TaskDefinitions.TaskPath(TaskDefinitions.AutoStartTaskName);

    /// <summary>Whether the auto-start task exists and is enabled. GetTask, not FindTask: the latter
    /// walks the entire task-folder tree (100–500 ms), and this is read on every tray-menu
    /// refresh.</summary>
    internal static bool IsAutoStartEnabled()
    {
        try
        {
            using var ts = new TaskService();
            using var task = ts.GetTask(TaskPath);
            return task?.Enabled == true;
        }
        catch { return false; }
    }

    /// <summary>Creates or removes the auto-start task for the current user. It runs at logon with
    /// highest privileges, bypassing the UAC prompt.</summary>
    internal static void SetAutoStart(bool enable)
    {
        using var ts = new TaskService();

        if (!enable)
        {
            ts.RootFolder.DeleteTask(TaskDefinitions.AutoStartTaskName, exceptionOnNotExists: false);
            return;
        }

        string exePath = Environment.ProcessPath ?? GetMainModulePath()
            ?? throw new InvalidOperationException("Cannot determine executable path for auto-start task.");

        TaskIdentity user = TaskIdentity.Current()
            ?? throw new InvalidOperationException("Cannot determine the current user for auto-start task.");

        // Throws on failure by design: the user asked for this, so a silent no-op would leave the
        // menu's check mark lying about the next boot.
        using TaskDefinition td = TaskDefinitions.BuildAutoStart(ts, exePath, user);
        TaskDefinitions.Register(ts, TaskDefinitions.AutoStartTaskName, td);
    }

    /// <summary>Fallback executable-path lookup for when <see cref="Environment.ProcessPath"/> is
    /// null.</summary>
    private static string? GetMainModulePath()
    {
        using var proc = System.Diagnostics.Process.GetCurrentProcess();
        return proc.MainModule?.FileName;
    }
}
