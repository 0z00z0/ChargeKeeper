using System.Text.RegularExpressions;
using ChargeKeeper.Helpers;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The scheduled-task names the installer script and the application must agree on. A structural
/// assertion over the Inno Setup script's own text: nothing here runs the installer.
/// </summary>
public class InstallerTaskNameTests
{
    private static readonly string Script =
        File.ReadAllText(RepoFiles.Find(Path.Combine("installer", "ChargeKeeper.iss")));

    private static string Define(string name)
    {
        var m = Regex.Match(Script, $@"#define\s+{name}\s+""([^""]*)""");
        Assert.True(m.Success, $"Could not find the #define for {name} in the installer script.");
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
}
