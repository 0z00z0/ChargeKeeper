using ChargeKeeper.Helpers;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The argv half of the startup decision, split out from Program.Main so it can be tested without
/// spawning the process whose fate it decides. Both failure modes are silent: a misread watchdog
/// probe either boots the WinUI stack every five minutes or refuses to resurrect a dead tray, and a
/// misread retry count either freezes a user launch or loses the self-heal relaunch race.
/// </summary>
public class StartupArgsTests
{
    // Args are Environment.GetCommandLineArgs()-shaped: element 0 is always the exe path.
    private const string Exe = @"C:\Program Files\ChargeKeeper\ChargeKeeper.exe";

    [Fact]
    public void PlainLaunch_IsNothingInParticular()
    {
        // The AutoStart logon task passes NO arguments, so this is also what a sign-in looks like.
        var startup = StartupArgs.Parse([Exe]);

        Assert.False(startup.IsDebugCommand);
        Assert.False(startup.IsWatchdogProbe);
        Assert.False(startup.IsAutoRelaunch);
    }

    [Fact]
    public void WatchdogArg_IsProbe()
    {
        var startup = StartupArgs.Parse([Exe, "--watchdog-relaunch"]);

        Assert.True(startup.IsWatchdogProbe);
        Assert.False(startup.IsAutoRelaunch);
    }

    [Fact]
    public void AutoRelaunchArg_IsAutoRelaunch()
    {
        var startup = StartupArgs.Parse([Exe, StartupArgs.AutoRelaunchArg]);

        Assert.True(startup.IsAutoRelaunch);
        Assert.False(startup.IsWatchdogProbe);
    }

    [Fact]
    public void ProbeAndRelaunchArgsAreDistinct()
    {
        // One means "check whether the app is gone", the other "the app just died and this is the
        // replacement", and they get opposite retry budgets below.
        Assert.NotEqual(StartupArgs.AutoRelaunchArg, TaskDefinitions.WatchdogArg);
    }

    [Fact]
    public void DebugCommand_IsRecognised()
    {
        // The arg-shape rules belong to CrashDumps.ParseDebugCommand; this only pins that a /debug
        // launch is flagged, which is what keeps Program.Main from booting XAML for it.
        Assert.True(StartupArgs.Parse([Exe, "/debug"]).IsDebugCommand);
        Assert.True(StartupArgs.Parse([Exe, "/debug", "off"]).IsDebugCommand);
        Assert.False(StartupArgs.Parse([Exe]).IsDebugCommand);
    }

    [Fact]
    public void InternalSpawnArgsAreNotDebugCommands()
    {
        // Read as /debug, a probe or self-heal relaunch would exit instead of doing its job and
        // silently end the tray app's resurrection path.
        Assert.False(StartupArgs.Parse([Exe, "--watchdog-relaunch"]).IsDebugCommand);
        Assert.False(StartupArgs.Parse([Exe, StartupArgs.AutoRelaunchArg]).IsDebugCommand);
    }

    [Fact]
    public void ExePathIsNotMistakenForAnArgument()
    {
        // Element 0 is matched like any other token; a build output under a folder named after a
        // switch must not read as that switch.
        Assert.False(StartupArgs.Parse([@"C:\src\--auto-relaunch\ChargeKeeper.exe"]).IsAutoRelaunch);
    }

    [Fact]
    public void AutoRelaunch_KeepsTheFullThreeSecondRetry()
    {
        // The self-heal relaunch is spawned while the old process may still be milliseconds from
        // releasing the mutex. Shortening this makes the replacement read the dying instance as
        // "already running" and exit, killing the tray for good.
        Assert.Equal(15, StartupArgs.Parse([Exe, StartupArgs.AutoRelaunchArg]).SingleInstanceAttempts);
    }

    [Fact]
    public void PlainLaunch_DoesNotPayTheSelfHealRetry()
    {
        // The mutex race only the auto-relaunch path can hit is not worth seconds of no icon, no
        // window and no message on a user's duplicate launch.
        int attempts = StartupArgs.Parse([Exe]).SingleInstanceAttempts;

        Assert.InRange(attempts, 2, 3);   // a couple: "Exit, then start it again" still has to work
    }

    [Fact]
    public void WatchdogProbe_GetsOneInstantAttempt()
    {
        // Finding a live instance is the probe's expected answer, not a race to wait out.
        Assert.Equal(1, StartupArgs.Parse([Exe, "--watchdog-relaunch"]).SingleInstanceAttempts);
    }
}
