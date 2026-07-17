using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32.TaskScheduler;

namespace ChargeKeeper.Helpers;

/// <summary>
/// The two forms of the current user's identity a task definition needs, and which field takes
/// which. They are NOT interchangeable, which is the reason this type exists rather than two loose
/// strings:
///
/// <list type="bullet">
/// <item><description><see cref="Sid"/> — for <see cref="TaskPrincipal.UserId"/>, which stores it
/// verbatim.</description></item>
/// <item><description><see cref="Name"/> — for trigger UserIds, because that is the form a trigger
/// STORES. Task Scheduler resolves whatever it is given to a qualified account name, so the SID and
/// the name converge once written (verified on the live tasks: the old SID-writing code produced
/// <c>&lt;UserId&gt;AzureAD\EspenLaget&lt;/UserId&gt;</c>, byte-identical to what
/// <see cref="WindowsIdentity.Name"/> yields). Writing the name is what makes that convergence
/// VISIBLE to <see cref="TaskDefinitions.Matches"/> instead of leaving the two writers to disagree
/// about a field the scheduler quietly rewrote under them — which is the ping-pong below. Do not
/// "simplify" this to the SID on the assumption the fields are interchangeable: they are not, and
/// the principal is the field that genuinely needs the SID.</description></item>
/// </list>
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
/// The single definition of what ChargeKeeper's two scheduled tasks must look like.
///
/// Root cause found 2026-07-08: the "LenovoTray AutoStart" logon task was created with Task
/// Scheduler's DEFAULTS, which include StopIfGoingOnBatteries=true (the scheduler hard-terminates
/// the instance the second AC power drops — i.e. at undock, matching the same-second "Power source
/// change" ↔ "Host window closed" ↔ dead-process forensics of 2026-07-06 and 2026-07-08),
/// DisallowStartIfOnBatteries=true (no auto-start on battery), and a 72h ExecutionTimeLimit (silent
/// kill after 3 days of uptime). The scheduler's stop sequence — close the windows politely, then
/// TerminateProcess — is exactly the observed signature: window-Closed log lines, then nothing (no
/// ShutdownStarting, no ProcessExit, no WER, no dump). <see cref="ApplyPowerSafeSettings"/> is what
/// keeps those defaults off, so every writer of these tasks MUST come through this class.
///
/// <para>Why one class: AutoStart has two writers — <see cref="TaskSchedulerHelper"/> (the user's
/// tray toggle) and <see cref="WatchdogTask"/> (the startup repair). While they built the task
/// independently the toggle wrote it WITHOUT the power-safe settings and WITHOUT
/// <see cref="DefStamp"/>, so the next startup's repair always saw a foreign definition and rewrote
/// it — every toggle cost a repair cycle. Both now register the definition this class returns, so
/// the repair's "already correct" check can no longer disagree with what the toggle just wrote.</para>
///
/// <para>Deliberately free of logging, I/O and app state: it maps inputs to a
/// <see cref="TaskDefinition"/> and nothing else, so the callers own policy (which tasks to write,
/// when, and what to log) and the definition itself stays directly assertable in tests.</para>
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

    /// <summary>
    /// Resume-from-standby. Power-Troubleshooter EventID 1 is logged by the kernel power manager
    /// after the resume completes, which is the point the app can actually be relaunched.
    /// </summary>
    private const string ResumeSubscription =
        "<QueryList><Query Id=\"0\" Path=\"System\"><Select Path=\"System\">"
        + "*[System[Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1]]"
        + "</Select></Query></QueryList>";

    /// <summary>Fixed and in the past: the repetition below is what schedules the probes, so the
    /// boundary only has to be a start point Task Scheduler considers already reached.</summary>
    private static readonly DateTime ProbeStartBoundary = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    /// <summary>
    /// Starts the app at logon. Created only by the user's tray toggle; the watchdog repairs it in
    /// place but never creates it (running at startup stays the user's choice).
    /// </summary>
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
    /// Relaunches the app if its process is gone. This is the backstop for the kill classes no
    /// in-process code can survive (external TerminateProcess, GPU/compositor teardown) — self-heal
    /// in OnProcessExit depends on code in the dying process running, which those kills skip by
    /// definition. A probe that finds a live instance exits instantly via the single-instance mutex;
    /// one that finds the hold-marker (user chose Exit from the tray menu) stays down.
    /// </summary>
    internal static TaskDefinition BuildWatchdog(TaskService ts, string exe, TaskIdentity user)
    {
        TaskDefinition td = ts.NewTask();
        td.RegistrationInfo.URI         = @"\" + WatchdogTaskName;
        td.RegistrationInfo.Description = WatchdogDescription;

        // Three triggers, each covering a gap the others leave: the repetition is the general
        // backstop, and unlock/resume close the window where a kill during sleep would otherwise
        // leave the app down for up to 5 minutes of the user actively using the machine.
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

    /// <summary>
    /// Both tasks live in the root folder, so the path is just a leading separator plus the name.
    /// Lives here, beside the names it is derived from, so the two callers cannot drift apart: if the
    /// tasks ever move into a subfolder, the reader and the writers must not disagree about where to
    /// look — the toggle would then report "off" against a task that exists and register a duplicate.
    /// </summary>
    internal static string TaskPath(string name) => @"\" + name;

    /// <summary>
    /// Registers <paramref name="definition"/> in the root folder. The single place that knows HOW
    /// these tasks are registered, for the same reason this class is the single place that knows what
    /// they look like: the definition and the credentials it is registered with are one decision.
    ///
    /// <para>The (null, null, InteractiveToken) credentials do NOT override the definition: the V2
    /// path passes the configured definition through untouched and resolves a null userId to the
    /// definition's own principal account, which <see cref="ApplyPowerSafeSettings"/> already pins to
    /// this user's SID. Change this triple and you change it for BOTH writers — which is the point.
    /// Changing it for only one is how the repair ping-pong came back.</para>
    ///
    /// <para>Throws on failure. Callers own the error policy: the tray toggle surfaces it (the user
    /// asked for this and deserves to know it failed), while the startup repair logs and continues
    /// (a lost safety net must never cost the app its startup).</para>
    /// </summary>
    internal static void Register(TaskService ts, string name, TaskDefinition definition) =>
        ts.RootFolder.RegisterTaskDefinition(
            name, definition,
            TaskCreation.CreateOrUpdate,   // overwrite an existing/stale definition
            userId:    null,
            password:  null,
            logonType: TaskLogonType.InteractiveToken);

    /// <summary>
    /// True when <paramref name="task"/> already carries this definition for this exe — i.e. there
    /// is nothing to rewrite. Matches on the stamp plus the target exe, so an upgrade that moves the
    /// exe still triggers a rewrite even though the stamp is unchanged.
    /// </summary>
    internal static bool Matches(TaskDefinition td, string exe) =>
        td.RegistrationInfo.Description?.Contains(DefStamp, StringComparison.Ordinal) == true
        && TargetsExe(td, exe);

    /// <summary>True when the task's action runs <paramref name="exe"/>.</summary>
    internal static bool TargetsExe(TaskDefinition td, string exe) =>
        td.Actions.OfType<ExecAction>().Any(a =>
            string.Equals(Unquote(a.Path), exe, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The path is stored quoted (see <see cref="NewExecAction"/>), so every read has to strip the
    /// quotes back off before comparing it to a real path.
    /// </summary>
    private static string Unquote(string? path) => path?.Trim().Trim('"') ?? "";

    /// <summary>
    /// Quoted deliberately: both install directories ("...\Lenovo Power Tray\") contain a space, and
    /// this is the form the registered tasks have carried since the 2026-07-08 fix. The scheduler
    /// stores Command verbatim, so leaving the quotes off would be a silent change to the live task.
    /// </summary>
    private static ExecAction NewExecAction(string exe, string? arguments) =>
        new($"\"{exe}\"", arguments);

    /// <summary>
    /// The settings that keep Task Scheduler from being the thing that kills the app.
    /// <see cref="TaskSettings.AllowHardTerminate"/>, <see cref="TaskSettings.StopIfGoingOnBatteries"/>
    /// and the zero <see cref="TaskSettings.ExecutionTimeLimit"/> are the 2026-07-08 undock fix —
    /// see the class remarks before changing any of them.
    /// </summary>
    private static void ApplyPowerSafeSettings(TaskDefinition td, TaskIdentity user)
    {
        // "Author" is the id the live tasks carry (<Principal id="Author"> / <Actions
        // Context="Author">); the library leaves both blank unless asked.
        td.Principal.Id        = PrincipalId;
        td.Actions.Context     = PrincipalId;
        td.Principal.UserId    = user.Sid;
        td.Principal.LogonType = TaskLogonType.InteractiveToken;
        td.Principal.RunLevel  = TaskRunLevel.Highest;      // elevated, no UAC prompt

        // Every line below overrides a default that is actively hostile to this app: a virgin
        // definition carries DisallowStartIfOnBatteries=true, StopIfGoingOnBatteries=true,
        // AllowHardTerminate=true and ExecutionTimeLimit=PT72H — the exact set behind the undock
        // kills. None of them may be dropped as "surely the default".
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
