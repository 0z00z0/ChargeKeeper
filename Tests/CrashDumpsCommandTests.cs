using ChargeKeeper.Helpers;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// Covers the two halves of the /debug command that can be tested in isolation:
/// <c>CrashDumps.ParseDebugCommand</c> (pure — split out from the registry writes precisely so the
/// arg-shape rules can be tested at all) and <c>CrashDumps.SetMarker</c>, which takes its path as a
/// parameter so these tests can drive it against a temp folder and never the real %AppData% marker.
/// </summary>
public class CrashDumpsCommandTests
{
    // Args are Environment.GetCommandLineArgs()-shaped: element 0 is always the exe path.
    private const string Exe = @"C:\Program Files\ChargeKeeper\ChargeKeeper.exe";

    [Fact]
    public void Parse_NoSwitch_IsNone()
    {
        // The overwhelmingly common case — AutoStart, a plain user launch — must leave the stored
        // intent alone rather than read "absent" as "off". Reading it as "off" is exactly the bug
        // this redesign replaced: the AutoStart logon task passes NO arguments, so every sign-in
        // disarmed the dumps the user had opted into.
        Assert.Equal(CrashDumps.DebugCommand.None, CrashDumps.ParseDebugCommand([Exe]));
    }

    [Fact]
    public void Parse_InternalSpawnArgs_IsNone()
    {
        // The watchdog probe and the self-heal relaunch must be indistinguishable from any other
        // normal launch here: neither may flip the arming state.
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
        // No console is attached to this windowed app, so a usage error has nowhere to go. "/debug"
        // plus noise resolves to the intent the user plainly expressed rather than silently nothing.
        Assert.Equal(CrashDumps.DebugCommand.Arm, CrashDumps.ParseDebugCommand([Exe, "/debug", "yes"]));
    }

    [Fact]
    public void Parse_OffOnlyCountsImmediatelyAfterDebug()
    {
        // "off" is positional: it disarms only as /debug's value, never as a stray later token.
        Assert.Equal(CrashDumps.DebugCommand.Arm, CrashDumps.ParseDebugCommand([Exe, "/debug", "on", "off"]));
        // ...and never on its own, without /debug at all.
        Assert.Equal(CrashDumps.DebugCommand.None, CrashDumps.ParseDebugCommand([Exe, "off"]));
    }

    [Fact]
    public void Parse_ExeNameContainingDebug_IsNotASwitch()
    {
        // Element 0 is a path, matched like any other token — a build output under a "debug" folder
        // must not read as the switch. (It cannot equal "/debug", which is the point of the check.)
        Assert.Equal(CrashDumps.DebugCommand.None,
            CrashDumps.ParseDebugCommand([@"C:\src\ChargeKeeper\bin\Debug\ChargeKeeper.exe"]));
    }

    /// <summary>
    /// Runs <paramref name="body"/> against a marker path inside a throwaway folder. The real marker
    /// lives in %AppData%\ChargeKeeper — a test that touched it would arm or disarm crash dumps on
    /// the machine running the suite.
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
        // Presence IS the stored intent — nothing reads the contents — so these two states are the
        // whole contract that CrashDumps.DumpsEnabled reads back on a release build.
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
        // AppPaths deliberately never creates %AppData%\ChargeKeeper; each writer does it lazily. A
        // /debug command can be the very first thing to write there, so arming must not depend on
        // some earlier component having made the folder.
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
        // /debug is a command a human retypes; arming twice or disarming what was never armed are
        // both ordinary, and neither may throw (this runs in a windowed app with no console).
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
