using ChargeKeeper.Helpers;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The argv half of the startup decision, split out from Program.Main so it can be tested without
/// spawning the process whose fate it decides. A misread watchdog probe either boots the WinUI stack
/// every five minutes or refuses to resurrect a dead tray.
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
    }

    [Fact]
    public void WatchdogArg_IsProbe()
    {
        var startup = StartupArgs.Parse([Exe, "--watchdog-relaunch"]);

        Assert.True(startup.IsWatchdogProbe);
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
    public void WatchdogProbeArgIsNotADebugCommand()
    {
        // Read as /debug, a probe would exit instead of doing its job and silently end the tray
        // app's resurrection path.
        Assert.False(StartupArgs.Parse([Exe, "--watchdog-relaunch"]).IsDebugCommand);
    }

    [Fact]
    public void PlainLaunch_GetsTheShortRetry()
    {
        // A couple of quick attempts: "Exit, then start it again" still has to work.
        int attempts = StartupArgs.Parse([Exe]).SingleInstanceAttempts;

        Assert.InRange(attempts, 2, 3);
    }

    [Fact]
    public void WatchdogProbe_GetsOneInstantAttempt()
    {
        // Finding a live instance is the probe's expected answer, not a race to wait out.
        Assert.Equal(1, StartupArgs.Parse([Exe, "--watchdog-relaunch"]).SingleInstanceAttempts);
    }
}
