using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32.TaskScheduler;

namespace ChargeKeeper.Helpers;

/// <summary>
/// The two forms of the current user's identity a task definition needs. They are not
/// interchangeable: <see cref="Sid"/> goes to <see cref="TaskPrincipal.UserId"/>, which stores it
/// verbatim, while trigger UserIds take <see cref="Name"/>, because a qualified account name is the
/// form a trigger stores — writing a SID there leaves the scheduler to rewrite the field underneath
/// the writers, so <see cref="TaskDefinitions.Matches"/> never sees its own output.
/// </summary>
internal readonly record struct TaskIdentity(string Sid, string Name)
{
    internal static TaskIdentity? Current()
    {
        using WindowsIdentity me = WindowsIdentity.GetCurrent();
        string? sid = me.User?.Value;
        return string.IsNullOrEmpty(sid) || string.IsNullOrEmpty(me.Name) ? null : new(sid, me.Name);
    }
}

/// <summary>
/// The single definition of what ChargeKeeper's two scheduled tasks must look like. Both writers of
/// the AutoStart task — the tray toggle in <see cref="TaskSchedulerHelper"/> and the startup repair
/// in <see cref="WatchdogTask"/> — must come through here, or the repair sees a foreign definition
/// and rewrites what the toggle just wrote. <see cref="ApplyPowerSafeSettings"/> is the reason it
/// matters: Task Scheduler's defaults hard-terminate the app at undock.
/// <para>Free of logging, I/O and app state, so the callers own policy and the definition stays
/// directly assertable in tests.</para>
/// </summary>
internal static class TaskDefinitions
{
    internal const string WatchdogArg = "--watchdog-relaunch";

    internal const string AutoStartTaskName = "ChargeKeeper AutoStart";
    internal const string WatchdogTaskName  = "ChargeKeeper Watchdog";

    /// <summary>Marks a task as carrying the definition below. Bump to force a rewrite of both
    /// definitions on the next startup.</summary>
    internal const string DefStamp = "[ChargeKeeper def-v1]";

    private const string PrincipalId = "Author";

    internal const string AutoStartDescription =
        $"Starts ChargeKeeper at logon, elevated, with power-safe settings. {DefStamp}";

    internal const string WatchdogDescription =
        "Relaunches ChargeKeeper if its process is gone (probe exits instantly when it is running, "
        + $"or when the user exited via the tray menu). {DefStamp}";

    /// <summary>Resume-from-standby. Power-Troubleshooter EventID 1 is logged after the resume
    /// completes, which is the point the app can actually be relaunched.</summary>
    private const string ResumeSubscription =
        "<QueryList><Query Id=\"0\" Path=\"System\"><Select Path=\"System\">"
        + "*[System[Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1]]"
        + "</Select></Query></QueryList>";

    /// <summary>Fixed and in the past: the repetition below is what schedules the probes, so the
    /// boundary only has to be a start point Task Scheduler considers already reached.</summary>
    private static readonly DateTime ProbeStartBoundary = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    /// <summary>Starts the app at logon. Created only by the user's tray toggle; the watchdog repairs
    /// it in place but never creates it, so running at startup stays the user's choice.</summary>
    internal static TaskDefinition BuildAutoStart(TaskService ts, string exe, TaskIdentity user)
    {
        TaskDefinition td = ts.NewTask();
        td.RegistrationInfo.URI         = @"\" + AutoStartTaskName;
        td.RegistrationInfo.Description = AutoStartDescription;
        td.Triggers.Add(new LogonTrigger { UserId = user.Name });
        td.Actions.Add(NewExecAction(exe, arguments: null));
        ApplyPowerSafeSettings(td, user);
        return td;
    }

    /// <summary>
    /// Relaunches the app if its process is gone — the backstop for kills no in-process code can
    /// survive, since self-heal in OnProcessExit needs code in the dying process to run. A probe that
    /// finds a live instance exits via the single-instance mutex; one that finds the hold marker
    /// stays down.
    /// </summary>
    internal static TaskDefinition BuildWatchdog(TaskService ts, string exe, TaskIdentity user)
    {
        TaskDefinition td = ts.NewTask();
        td.RegistrationInfo.URI         = @"\" + WatchdogTaskName;
        td.RegistrationInfo.Description = WatchdogDescription;

        // The repetition is the general backstop; unlock and resume close the window where a kill
        // during sleep would leave the app down for up to 5 minutes of active use.
        td.Triggers.Add(new TimeTrigger
        {
            StartBoundary = ProbeStartBoundary,
            Repetition = { Interval = TimeSpan.FromMinutes(5) },
        });
        td.Triggers.Add(new SessionStateChangeTrigger
        {
            StateChange = TaskSessionStateChangeType.SessionUnlock,
            UserId      = user.Name,
            Delay       = TimeSpan.FromSeconds(5),
        });
        td.Triggers.Add(new EventTrigger
        {
            Subscription = ResumeSubscription,
            Delay        = TimeSpan.FromSeconds(15),
        });

        td.Actions.Add(NewExecAction(exe, WatchdogArg));
        ApplyPowerSafeSettings(td, user);
        return td;
    }

    /// <summary>Both tasks live in the root folder. Composed here, beside the names, so the reader
    /// and the writers cannot disagree about where to look.</summary>
    internal static string TaskPath(string name) => @"\" + name;

    /// <summary>Registers <paramref name="definition"/> in the root folder, for both writers at once.
    /// The (null, null, InteractiveToken) credentials do not override it: a null userId resolves to
    /// the definition's own principal. Throws on failure; callers own the error policy.</summary>
    internal static void Register(TaskService ts, string name, TaskDefinition definition) =>
        ts.RootFolder.RegisterTaskDefinition(
            name, definition,
            TaskCreation.CreateOrUpdate,   // overwrite an existing/stale definition
            userId:    null,
            password:  null,
            logonType: TaskLogonType.InteractiveToken);

    /// <summary>True when the task already carries this definition for this exe, so there is nothing
    /// to rewrite. Matching on the exe as well as the stamp means an upgrade that moves the exe still
    /// triggers a rewrite.</summary>
    internal static bool Matches(TaskDefinition td, string exe) =>
        td.RegistrationInfo.Description?.Contains(DefStamp, StringComparison.Ordinal) == true
        && TargetsExe(td, exe);

    /// <summary>True when the task's action runs <paramref name="exe"/>.</summary>
    internal static bool TargetsExe(TaskDefinition td, string exe) =>
        td.Actions.OfType<ExecAction>().Any(a =>
            string.Equals(Unquote(a.Path), exe, StringComparison.OrdinalIgnoreCase));

    /// <summary>The path is stored quoted (see <see cref="NewExecAction"/>), so every read has to
    /// strip the quotes back off before comparing it to a real path.</summary>
    private static string Unquote(string? path) => path?.Trim().Trim('"') ?? "";

    /// <summary>Quoted because both install directories contain a space, and because the scheduler
    /// stores Command verbatim — dropping the quotes would silently change the live task.</summary>
    private static ExecAction NewExecAction(string exe, string? arguments) =>
        new($"\"{exe}\"", arguments);

    /// <summary>The settings that keep Task Scheduler from being the thing that kills the app.</summary>
    private static void ApplyPowerSafeSettings(TaskDefinition td, TaskIdentity user)
    {
        // "Author" is the id the live tasks carry; the library leaves it blank unless asked.
        td.Principal.Id        = PrincipalId;
        td.Actions.Context     = PrincipalId;
        td.Principal.UserId    = user.Sid;
        td.Principal.LogonType = TaskLogonType.InteractiveToken;
        td.Principal.RunLevel  = TaskRunLevel.Highest;      // elevated, no UAC prompt

        // A virgin definition carries DisallowStartIfOnBatteries=true, StopIfGoingOnBatteries=true,
        // AllowHardTerminate=true and ExecutionTimeLimit=PT72H — the set that hard-kills the app at
        // undock. None of the overrides below may be dropped as "surely the default".
        TaskSettings s = td.Settings;
        s.MultipleInstances           = TaskInstancesPolicy.IgnoreNew;
        s.DisallowStartIfOnBatteries  = false;
        s.StopIfGoingOnBatteries      = false;
        s.AllowHardTerminate          = false;
        s.StartWhenAvailable          = true;
        s.IdleSettings.StopOnIdleEnd  = false;
        s.IdleSettings.RestartOnIdle  = false;
        s.AllowDemandStart            = true;
        s.Enabled                     = true;
        s.Hidden                      = false;
        s.RunOnlyIfIdle               = false;
        s.WakeToRun                   = false;
        s.ExecutionTimeLimit          = TimeSpan.Zero;      // no 72h silent kill
        s.Priority                    = ProcessPriorityClass.Normal;
    }
}
