using System.Security.Principal;
using Microsoft.Win32.TaskScheduler;

namespace ChargeKeeper.Helpers;

/// <summary>
/// Manages the Windows Task Scheduler entry that auto-starts ChargeKeeper at logon.
/// A scheduled task (rather than a Run key) is required because the app runs elevated —
/// Run-key entries for elevated apps trigger a UAC prompt on every boot.
/// </summary>
internal static class TaskSchedulerHelper
{
    private const string TaskName = "ChargeKeeper AutoStart";

    /// <summary>Returns <c>true</c> when the auto-start task exists and is enabled.</summary>
    internal static bool IsAutoStartEnabled()
    {
        try
        {
            using var ts   = new TaskService();
            var task       = ts.FindTask(TaskName);
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
            ts.RootFolder.DeleteTask(TaskName, exceptionOnNotExists: false);
            return;
        }

        // Resolve the path of the running executable.
        var exePath = Environment.ProcessPath ?? GetMainModulePath()
            ?? throw new InvalidOperationException("Cannot determine executable path for auto-start task.");

        var td = ts.NewTask();
        td.RegistrationInfo.Description = "ChargeKeeper — starts minimised to system tray";
        td.Principal.RunLevel           = TaskRunLevel.Highest;  // elevated, no UAC prompt

        // Trigger: run when this specific user logs on
        td.Triggers.Add(new LogonTrigger { UserId = WindowsIdentity.GetCurrent().Name });
        td.Actions.Add(new ExecAction(exePath));

        // Battery-friendly settings
        td.Settings.ExecutionTimeLimit        = TimeSpan.Zero; // no time limit
        td.Settings.DisallowStartIfOnBatteries = false;
        td.Settings.StopIfGoingOnBatteries     = false;

        ts.RootFolder.RegisterTaskDefinition(
            TaskName, td,
            TaskCreation.CreateOrUpdate,
            userId:    null,
            password:  null,
            logonType: TaskLogonType.InteractiveToken);
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
