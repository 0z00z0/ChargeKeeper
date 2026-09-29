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
    public void WatchdogProbeArgIsNotADebugCommand()
    {
        // Read as /debug, a probe would exit instead of doing its job and silently end the tray
        // app's resurrection path.
        Assert.False(StartupArgs.Parse([Exe, "--watchdog-relaunch"]).IsDebugCommand);
    }
}
