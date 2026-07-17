using ChargeKeeper.Helpers;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// Covers <c>StartupArgs</c> — the argv half of the startup decision, split out from Program.Main
/// precisely so it can be tested without spawning the process it decides the fate of.
///
/// <para>Two things ride on this being right, and both fail SILENTLY (no crash, no log) if it is
/// wrong: a misread watchdog probe either boots the whole WinUI stack ~288 times a day or refuses
/// to resurrect a dead tray, and a misread retry count either freezes a user launch for 3 seconds
/// or breaks the self-heal relaunch race the retry exists for.</para>
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
        // They must never collide: one means "check whether the app is gone", the other means "the
        // app just died and this IS the replacement" — and they get opposite retry budgets below.
        Assert.NotEqual(StartupArgs.AutoRelaunchArg, WatchdogTask.WatchdogArg);
    }

    [Fact]
    public void DebugCommand_IsRecognised()
    {
        // Delegated to CrashDumps.ParseDebugCommand (its own tests own the arg-shape rules); this
        // only pins that a /debug launch is flagged as a command at all, which is what keeps
        // Program.Main from booting XAML for it.
        Assert.True(StartupArgs.Parse([Exe, "/debug"]).IsDebugCommand);
        Assert.True(StartupArgs.Parse([Exe, "/debug", "off"]).IsDebugCommand);
        Assert.False(StartupArgs.Parse([Exe]).IsDebugCommand);
    }

    [Fact]
    public void InternalSpawnArgsAreNotDebugCommands()
    {
        // A probe or a self-heal relaunch must never be mistaken for the /debug command — it would
        // exit instead of doing its job, silently ending the tray app's resurrection path.
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
        // The self-heal relaunch is spawned while the OLD process may still be milliseconds from
        // releasing the mutex. This is the ONE path the retry exists for; shortening it here would
        // mean the replacement reads the dying instance as "already running" and exits, killing the
        // tray for good.
        Assert.Equal(15, StartupArgs.Parse([Exe, StartupArgs.AutoRelaunchArg]).SingleInstanceAttempts);
    }

    [Fact]
    public void PlainLaunch_DoesNotPayTheSelfHealRetry()
    {
        // A user's duplicate launch used to sit through all 15 attempts — 3 s of no icon, no window
        // and no message — to cover a race that only the auto-relaunch path can hit.
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
