using Microsoft.Win32.TaskScheduler;

namespace ChargeKeeper.Helpers;

/// <summary>
/// The user's auto-start toggle: creates and removes the logon task behind the tray menu's
/// "Start with Windows".
///
/// A scheduled task (rather than a Run key) is required because the app runs elevated —
/// Run-key entries for elevated apps trigger a UAC prompt on every boot.
///
/// <para>The definition itself comes from <see cref="TaskDefinitions"/>, which
/// <see cref="WatchdogTask"/>'s startup repair also builds from. That shared definition is what
/// stops the two from disagreeing: this class used to write the task with its own description and
/// without the power-safe settings, so the repair saw a foreign definition on the next start and
/// rewrote it — every toggle bought a repair cycle, and the task spent the gap carrying the
/// scheduler defaults that hard-kill the app at undock.</para>
/// </summary>
internal static class TaskSchedulerHelper
{
    /// <summary>
    /// The task's full path — composed by <see cref="TaskDefinitions.TaskPath"/> rather than here, so
    /// this reader cannot disagree with the writers about where the task lives.
    /// </summary>
    private static string TaskPath => TaskDefinitions.TaskPath(TaskDefinitions.AutoStartTaskName);

    /// <summary>
    /// Returns <c>true</c> when the auto-start task exists and is enabled.
    ///
    /// <para>GetTask (a direct lookup by path) rather than FindTask (which walks the ENTIRE
    /// task-folder tree recursively, 100–500 ms on a machine with the usual vendor/Windows task
    /// libraries). This is read on every tray-menu state refresh, so the tree walk was pure cost —
    /// spent to find a task whose exact path we chose ourselves.</para>
    /// </summary>
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

    /// <summary>
    /// Creates or removes the auto-start task for the current user.
    /// The task runs at logon with highest privileges, bypassing the UAC prompt.
    /// </summary>
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

        // Throws on failure by design — the user asked for this, so a silent no-op would leave the
        // menu's check mark lying about the next boot.
        using TaskDefinition td = TaskDefinitions.BuildAutoStart(ts, exePath, user);
        TaskDefinitions.Register(ts, TaskDefinitions.AutoStartTaskName, td);
    }

    /// <summary>
    /// Fallback executable-path lookup used only when <see cref="Environment.ProcessPath"/> is null.
    /// Disposes the <see cref="System.Diagnostics.Process"/> handle it opens.
    /// </summary>
    private static string? GetMainModulePath()
    {
        using var proc = System.Diagnostics.Process.GetCurrentProcess();
        return proc.MainModule?.FileName;
    }
}
