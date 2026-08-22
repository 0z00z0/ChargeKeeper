using ChargeKeeper.Helpers;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The two halves of the /debug command that can be tested without touching the machine:
/// <c>ParseDebugCommand</c>, which is pure, and <c>SetMarker</c>, which takes its path as a
/// parameter so it can be driven against a temp folder instead of the real %AppData% marker.
/// </summary>
public class CrashDumpsCommandTests
{
    // Args are Environment.GetCommandLineArgs()-shaped: element 0 is always the exe path.
    private const string Exe = @"C:\Program Files\ChargeKeeper\ChargeKeeper.exe";

    [Fact]
    public void Parse_NoSwitch_IsNone()
    {
        // The common case — AutoStart, a plain user launch — must leave the stored intent alone.
        // The logon task passes no arguments, so reading "absent" as "off" would disarm the dumps
        // the user opted into on every sign-in.
        Assert.Equal(CrashDumps.DebugCommand.None, CrashDumps.ParseDebugCommand([Exe]));
    }

    [Fact]
    public void Parse_InternalSpawnArgs_IsNone()
    {
        // The watchdog probe and the self-heal relaunch must not flip the arming state.
        Assert.Equal(CrashDumps.DebugCommand.None, CrashDumps.ParseDebugCommand([Exe, "--watchdog-relaunch"]));
        Assert.Equal(CrashDumps.DebugCommand.None, CrashDumps.ParseDebugCommand([Exe, "--auto-relaunch"]));
    }

    [Fact]
    public void Parse_BareDebug_IsArm()
    {
        Assert.Equal(CrashDumps.DebugCommand.Arm, CrashDumps.ParseDebugCommand([Exe, "/debug"]));
    }

    [Fact]
    public void Parse_DebugOn_IsArm()
    {
        Assert.Equal(CrashDumps.DebugCommand.Arm, CrashDumps.ParseDebugCommand([Exe, "/debug", "on"]));
    }

    [Fact]
    public void Parse_DebugOff_IsDisarm()
    {
        Assert.Equal(CrashDumps.DebugCommand.Disarm, CrashDumps.ParseDebugCommand([Exe, "/debug", "off"]));
    }

    [Fact]
    public void Parse_IsCaseInsensitive()
    {
        // Typed by a human, and Windows switches conventionally ignore case.
        Assert.Equal(CrashDumps.DebugCommand.Arm,    CrashDumps.ParseDebugCommand([Exe, "/DEBUG"]));
        Assert.Equal(CrashDumps.DebugCommand.Disarm, CrashDumps.ParseDebugCommand([Exe, "/Debug", "OFF"]));
    }

    [Fact]
    public void Parse_UnknownValueAfterDebug_IsArm()
    {
        // A windowed app has no console, so a usage error has nowhere to go: /debug plus noise
        // resolves to the intent the user expressed rather than to silently nothing.
        Assert.Equal(CrashDumps.DebugCommand.Arm, CrashDumps.ParseDebugCommand([Exe, "/debug", "yes"]));
    }

    [Fact]
    public void Parse_OffOnlyCountsImmediatelyAfterDebug()
    {
        // "off" is positional: it disarms only as /debug's value, never as a stray later token and
        // never on its own.
        Assert.Equal(CrashDumps.DebugCommand.Arm, CrashDumps.ParseDebugCommand([Exe, "/debug", "on", "off"]));
        Assert.Equal(CrashDumps.DebugCommand.None, CrashDumps.ParseDebugCommand([Exe, "off"]));
    }

    [Fact]
    public void Parse_ExeNameContainingDebug_IsNotASwitch()
    {
        // Element 0 is a path matched like any other token, so a build output under a "debug" folder
        // must not read as the switch.
        Assert.Equal(CrashDumps.DebugCommand.None,
            CrashDumps.ParseDebugCommand([@"C:\src\ChargeKeeper\bin\Debug\ChargeKeeper.exe"]));
    }

    /// <summary>
    /// Runs <paramref name="body"/> against a marker path in a throwaway folder: touching the real
    /// marker would arm or disarm crash dumps on the machine running the suite.
    /// </summary>
    private static void WithTempMarker(Action<string> body)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"ChargeKeeperTests-{Guid.NewGuid():N}");
        try { body(Path.Combine(dir, "crash-dumps-armed.marker")); }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ } }
    }

    [Fact]
    public void SetMarker_ArmCreates_DisarmRemoves()
    {
        // Presence is the stored intent; nothing reads the file's contents.
        WithTempMarker(path =>
        {
            Assert.False(File.Exists(path));

            CrashDumps.SetMarker(path, arm: true);
            Assert.True(File.Exists(path));

            CrashDumps.SetMarker(path, arm: false);
            Assert.False(File.Exists(path));
        });
    }

    [Fact]
    public void SetMarker_CreatesMissingDataDirectory()
    {
        // AppPaths never creates %AppData%\ChargeKeeper; each writer does it lazily, and /debug can
        // be the first thing to write there.
        WithTempMarker(path =>
        {
            Assert.False(Directory.Exists(Path.GetDirectoryName(path)!));
            CrashDumps.SetMarker(path, arm: true);
            Assert.True(File.Exists(path));
        });
    }

    [Fact]
    public void SetMarker_IsIdempotent()
    {
        // A human retypes /debug, so arming twice or disarming what was never armed must not throw
        // in an app with no console to report it.
        WithTempMarker(path =>
        {
            CrashDumps.SetMarker(path, arm: false);   // never armed
            Assert.False(File.Exists(path));

            CrashDumps.SetMarker(path, arm: true);
            CrashDumps.SetMarker(path, arm: true);
            Assert.True(File.Exists(path));

            CrashDumps.SetMarker(path, arm: false);
            CrashDumps.SetMarker(path, arm: false);
            Assert.False(File.Exists(path));
        });
    }
}
