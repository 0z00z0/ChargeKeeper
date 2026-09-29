using ChargeKeeper.Helpers;
using Microsoft.Win32.TaskScheduler;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// Locks down the scheduled-task definitions: the settings that stop Task Scheduler killing the
/// app, and the fields two writers must agree on so the startup repair stays idempotent.
///
/// <para>Definitions are only built here — <see cref="TaskService.NewTask"/> is an in-memory COM
/// object — so the machine's real tasks are untouched.</para>
/// </summary>
public class TaskDefinitionsTests
{
    private const string Exe = @"C:\Program Files\ChargeKeeper\ChargeKeeper.exe";
    private static readonly TaskIdentity User = new("S-1-5-21-1-2-3-1001", @"AzureAD\SomeUser");

    private static TaskDefinition Watchdog(TaskService ts)  => TaskDefinitions.BuildWatchdog(ts, Exe, User);
    private static TaskDefinition AutoStart(TaskService ts) => TaskDefinitions.BuildAutoStart(ts, Exe, User);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BothTasks_AreImmuneToTheSchedulersKillDefaults(bool watchdog)
    {
        // A virgin definition carries DisallowStartIfOnBatteries, StopIfGoingOnBatteries,
        // AllowHardTerminate and ExecutionTimeLimit=PT72H. The scheduler acts on all four, so
        // forgetting one kills the app at undock, or silently three days in.
        using var ts = new TaskService();
        TaskDefinition td = watchdog ? Watchdog(ts) : AutoStart(ts);

        Assert.False(td.Settings.AllowHardTerminate);
        Assert.False(td.Settings.StopIfGoingOnBatteries);
        Assert.False(td.Settings.DisallowStartIfOnBatteries);
        Assert.Equal(TimeSpan.Zero, td.Settings.ExecutionTimeLimit);
    }

    [Fact]
    public void Watchdog_RunsTheProbeArgument_AutoStartDoesNot()
    {
        using var ts = new TaskService();
        Assert.Equal(TaskDefinitions.WatchdogArg, ((ExecAction)Watchdog(ts).Actions[0]).Arguments);
        Assert.True(string.IsNullOrEmpty(((ExecAction)AutoStart(ts).Actions[0]).Arguments));
    }

    [Fact]
    public void Matches_RequiresBothTheStampAndTheExe()
    {
        // The stamp alone is not enough: an upgrade that moves the exe must still be rewritten, and
        // a task pointing at another exe must never be mistaken for this one.
        using var ts = new TaskService();

        Assert.False(TaskDefinitions.Matches(Watchdog(ts), @"C:\Elsewhere\ChargeKeeper.exe"));

        TaskDefinition unstamped = Watchdog(ts);
        unstamped.RegistrationInfo.Description = "Something a previous version wrote";
        Assert.False(TaskDefinitions.Matches(unstamped, Exe));
        Assert.True(TaskDefinitions.TargetsExe(unstamped, Exe));   // but it still points at the exe
    }
}
