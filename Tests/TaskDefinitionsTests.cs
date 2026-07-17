using ChargeKeeper.Helpers;
using Microsoft.Win32.TaskScheduler;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// Locks down the scheduled-task definitions. Every assertion here stands in for a real incident:
/// the 2026-07-06/08 undock kills (Task Scheduler's own defaults terminating the app), and the
/// AutoStart repair ping-pong caused by two writers disagreeing about the definition.
///
/// <para>These tests only BUILD definitions — <see cref="TaskService.NewTask"/> is an in-memory COM
/// object. Nothing is registered, so the machine's real tasks are untouched.</para>
/// </summary>
public class TaskDefinitionsTests
{
    private const string Exe = @"C:\Program Files\ChargeKeeper\ChargeKeeper.exe";
    private static readonly TaskIdentity User = new("S-1-5-21-1-2-3-1001", @"AzureAD\SomeUser");

    private static TaskDefinition Watchdog(TaskService ts)  => TaskDefinitions.BuildWatchdog(ts, Exe, User);
    private static TaskDefinition AutoStart(TaskService ts) => TaskDefinitions.BuildAutoStart(ts, Exe, User);

    /// <summary>
    /// The 2026-07-08 root cause. A virgin definition carries DisallowStartIfOnBatteries=true,
    /// StopIfGoingOnBatteries=true, AllowHardTerminate=true and ExecutionTimeLimit=PT72H; the
    /// scheduler acts on all four, so a definition that "forgets" any one of them is a definition
    /// that kills the app at undock (or silently, three days in).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BothTasks_AreImmuneToTheSchedulersKillDefaults(bool watchdog)
    {
        using var ts = new TaskService();
        TaskDefinition td = watchdog ? Watchdog(ts) : AutoStart(ts);

        Assert.False(td.Settings.AllowHardTerminate);
        Assert.False(td.Settings.StopIfGoingOnBatteries);
        Assert.False(td.Settings.DisallowStartIfOnBatteries);
        Assert.Equal(TimeSpan.Zero, td.Settings.ExecutionTimeLimit);
    }

    /// <summary>
    /// The two identity fields are not interchangeable, and the AutoStart repair ping-pong is what
    /// happens when a writer mixes them up: triggers store a resolved, qualified ACCOUNT NAME (the
    /// scheduler rewrites a SID into one), while the principal stores the SID verbatim. The old
    /// writers disagreed here — TaskSchedulerHelper wrote the name, WatchdogTask wrote the SID — so
    /// each startup's repair saw a "foreign" definition and rewrote what the toggle had just set.
    /// Pinning both fields is what makes Matches() able to say "already correct" and mean it.
    /// </summary>
    [Fact]
    public void Triggers_TakeTheAccountName_WhilePrincipalTakesTheSid()
    {
        using var ts = new TaskService();
        TaskDefinition wd = Watchdog(ts);

        Assert.Equal(User.Name, ((SessionStateChangeTrigger)wd.Triggers[1]).UserId);
        Assert.Equal(User.Name, ((LogonTrigger)AutoStart(ts).Triggers[0]).UserId);

        Assert.Equal(User.Sid, wd.Principal.UserId);
        Assert.Equal(TaskLogonType.InteractiveToken, wd.Principal.LogonType);
        Assert.Equal(TaskRunLevel.Highest, wd.Principal.RunLevel);
    }

    /// <summary>
    /// All three probes, with the delays that make them useful. The repetition is the general
    /// backstop; unlock and resume close the window in which a kill during sleep would leave the
    /// app down while the user is actively using the machine.
    /// </summary>
    [Fact]
    public void Watchdog_KeepsAllThreeProbes()
    {
        using var ts = new TaskService();
        TaskDefinition td = Watchdog(ts);

        Assert.Equal(TimeSpan.FromMinutes(5), td.Triggers.OfType<TimeTrigger>().Single().Repetition.Interval);

        SessionStateChangeTrigger unlock = td.Triggers.OfType<SessionStateChangeTrigger>().Single();
        Assert.Equal(TaskSessionStateChangeType.SessionUnlock, unlock.StateChange);
        Assert.Equal(TimeSpan.FromSeconds(5), unlock.Delay);

        EventTrigger resume = td.Triggers.OfType<EventTrigger>().Single();
        Assert.Equal(TimeSpan.FromSeconds(15), resume.Delay);
        Assert.Contains("Microsoft-Windows-Power-Troubleshooter", resume.Subscription);
        Assert.Contains("EventID=1", resume.Subscription);
    }

    [Fact]
    public void Watchdog_RunsTheProbeArgument_AutoStartDoesNot()
    {
        using var ts = new TaskService();
        Assert.Equal(TaskDefinitions.WatchdogArg, ((ExecAction)Watchdog(ts).Actions[0]).Arguments);
        Assert.True(string.IsNullOrEmpty(((ExecAction)AutoStart(ts).Actions[0]).Arguments));
    }

    /// <summary>Both install paths contain a space, and the registered tasks have always carried a
    /// quoted Command; dropping the quotes would silently change the live task.</summary>
    [Fact]
    public void ExecPath_IsQuoted_ButStillMatchesTheRawPath()
    {
        using var ts = new TaskService();
        TaskDefinition td = Watchdog(ts);

        Assert.Equal($"\"{Exe}\"", ((ExecAction)td.Actions[0]).Path);
        Assert.True(TaskDefinitions.TargetsExe(td, Exe));   // the quoting must not break the lookup
    }

    /// <summary>
    /// THE regression test for the repair ping-pong. TaskSchedulerHelper (the user's toggle) and
    /// WatchdogTask (the startup repair) now build from this one definition, so what the toggle
    /// writes is by construction what the repair considers correct. While they built the task
    /// independently, the toggle's version carried neither the stamp nor the power-safe settings,
    /// so every toggle cost a repair cycle on the next start.
    /// </summary>
    [Fact]
    public void AutoStart_AsWrittenByTheToggle_IsAlreadyWhatTheRepairWants()
    {
        using var ts = new TaskService();
        Assert.True(TaskDefinitions.Matches(AutoStart(ts), Exe));
        Assert.True(TaskDefinitions.Matches(Watchdog(ts), Exe));
    }

    /// <summary>
    /// Idempotence: the stamp alone is not enough — an upgrade that moves the exe must still be
    /// rewritten, and a task pointing at a different exe must never be mistaken for ours.
    /// </summary>
    [Fact]
    public void Matches_RequiresBothTheStampAndTheExe()
    {
        using var ts = new TaskService();

        Assert.False(TaskDefinitions.Matches(Watchdog(ts), @"C:\Elsewhere\ChargeKeeper.exe"));

        TaskDefinition unstamped = Watchdog(ts);
        unstamped.RegistrationInfo.Description = "Something a previous version wrote";
        Assert.False(TaskDefinitions.Matches(unstamped, Exe));
        Assert.True(TaskDefinitions.TargetsExe(unstamped, Exe));   // ...but it IS still our exe
    }

    [Fact]
    public void Descriptions_CarryTheStamp_SoTheRepairCanRecogniseThem()
    {
        Assert.Contains(TaskDefinitions.DefStamp, TaskDefinitions.AutoStartDescription);
        Assert.Contains(TaskDefinitions.DefStamp, TaskDefinitions.WatchdogDescription);
    }
}
