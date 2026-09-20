using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The three facts the unattended update rests on, none of which fails visibly when it drifts: an
/// update started from the application's own menu shows no wizard and no message box, the installer
/// script reads the switch the application passes, and the two sides name the same refusal file.
/// Nothing here runs the installer, and no test in this project can.
/// </summary>
public class UnattendedUpdateTests
{
    private static readonly string Script =
        File.ReadAllText(RepoFiles.Find(Path.Combine("installer", "ChargeKeeper.iss")));

    [Fact]
    public void TheSwitches_InstallSilentlyAndRaiseNoMessageBox()
    {
        // A silent switch without /SUPPRESSMSGBOXES still displays error boxes, which under a
        // silent run stand alone with no wizard behind them and no process left to own them.
        var arguments = UnattendedUpdate.Arguments(@"C:\log.txt");

        Assert.Contains("/SILENT", arguments);
        Assert.Contains("/SUPPRESSMSGBOXES", arguments);
        Assert.Contains("/NORESTART", arguments);
        Assert.Contains(UnattendedUpdate.StartedByApplicationSwitch, arguments);
        Assert.Contains(@"/LOG=""C:\log.txt""", arguments);
    }

    [Fact]
    public void TheInstallerScript_ReadsTheSwitchTheApplicationPasses()
    {
        // The script tells this flow from a winget or scheduled run by this switch alone. A name
        // that drifts leaves every branch keyed on it dead: no relaunch, no wait for the exit, and
        // no reason recorded when nothing installs.
        string name = UnattendedUpdate.StartedByApplicationSwitch.TrimStart('/').Split('=')[0];

        Assert.Contains($"{{param:{name}|0}}", Script, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRefusalFile_IsTheOneTheApplicationReads()
    {
        // Setup writes it and the next start reads it. Neither side can read the other's constant.
        Assert.Contains(UnattendedUpdate.RefusalFileName, Script, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOlderRunningVersion_ReportsAnUpdateThatDidNotComplete()
    {
        // The direction is the whole report, and it is only ever exercised by a real failed update:
        // inverted, every successful update would announce a failure and every failure would pass
        // unmentioned.
        Assert.Equal(UpdateVerdict.DidNotComplete, UnattendedUpdate.VerdictFor("1.43.0", "1.42.1"));
        Assert.Equal(UpdateVerdict.Installed,      UnattendedUpdate.VerdictFor("1.43.0", "1.43.0"));
        Assert.Equal(UpdateVerdict.Installed,      UnattendedUpdate.VerdictFor("1.43.0", "1.44.0"));
        Assert.Equal(UpdateVerdict.NothingHandedOver, UnattendedUpdate.VerdictFor(null, "1.42.1"));
    }
}
