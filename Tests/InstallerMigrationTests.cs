using System.Text.RegularExpressions;
using ChargeKeeper.Helpers;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// What can be asserted about the installer's move of an installation out of the retired product
/// folder. These are STRUCTURAL assertions over the Inno Setup script's own text: nothing here runs
/// the installer, and no test in this project can. They pin the two things that would otherwise
/// rot silently — the literals the script and the application must agree on, and the order in which
/// the migration removes things.
/// </summary>
public class InstallerMigrationTests
{
    private static readonly string Script =
        File.ReadAllText(RepoFiles.Find(Path.Combine("installer", "ChargeKeeper.iss")));

    private static string Define(string name) =>
        Match($@"#define\s+{name}\s+""([^""]*)""", $"the #define for {name}");

    /// <summary>A routine's text, from its header to the first line that closes it.</summary>
    private static string Body(string routine)
    {
        int start = Script.IndexOf(routine, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{routine}' is not in the installer script.");

        int end = Script.IndexOf("\nend;", start, StringComparison.Ordinal);
        Assert.True(end > start, $"'{routine}' has no closing end.");
        return Script[start..end];
    }

    private static string Match(string pattern, string what)
    {
        var m = Regex.Match(Script, pattern);
        Assert.True(m.Success, $"Could not find {what} in the installer script.");
        return m.Groups[1].Value;
    }

    [Fact]
    public void EveryTaskName_AgreesWithTheApplication()
    {
        // The installer deletes and re-points tasks the application writes. A name that drifts
        // leaves the installer editing nothing and the old task running.
        Assert.Equal(TaskDefinitions.AutoStartTaskName, Define("TaskName"));
        Assert.Contains($"WatchdogTaskName = '{TaskDefinitions.WatchdogTaskName}'", Script, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingIsRemovedUntilTheNewLocationIsVerified()
    {
        string guard = Body("function LegacyMigrationCanProceed");

        // The new executable is on disk...
        Assert.Contains(@"FileExists(ExpandConstant('{app}\{#AppExe}'))", guard, StringComparison.Ordinal);
        // ...the directory about to be deleted is not the one just installed into...
        Assert.Contains("CompareText(RemoveBackslashUnlessRoot(PreviousAppDir)", guard, StringComparison.Ordinal);
        // ...it really carries the retired name, and this run is a migration at all.
        Assert.Contains("IsLegacyDir(PreviousAppDir)", guard, StringComparison.Ordinal);
        Assert.Contains("MigratingFromLegacy", guard, StringComparison.Ordinal);
    }
}
