using System.Text.RegularExpressions;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// Guards the argument to TaskbarIcon.ForceCreate. The parameter is optional and defaults to true,
/// which puts the whole process into IDLE_PRIORITY_CLASS and EcoQoS for the rest of its life —
/// nothing reverses it, and the tray's left-click path then arrives seconds late under load.
/// Asserted on the text because the compiler cannot help: dropping the argument builds clean and
/// silently restores the throttling, the same reason NLogConfigTests reads its config as a string.
/// </summary>
public class TrayEfficiencyModeTests
{
    /// <summary>App.xaml.cs with its line comments stripped — one of them names ForceCreate() to
    /// warn readers off it, and a bare-call search must not match prose.</summary>
    private static string AppSourceWithoutComments()
    {
        string source = File.ReadAllText(RepoFiles.Find("App.xaml.cs"));
        return Regex.Replace(source, @"//[^\r\n]*", string.Empty);
    }

    [Fact]
    public void ForceCreate_TurnsEfficiencyModeOff()
    {
        Assert.Contains("ForceCreate(enablesEfficiencyMode: false)", AppSourceWithoutComments());
    }

    [Fact]
    public void ForceCreate_IsNeverCalledWithoutTheArgument()
    {
        // The default is the defect, so an argumentless call is the regression to catch.
        Assert.DoesNotMatch(new Regex(@"ForceCreate\(\s*\)"), AppSourceWithoutComments());
    }
}
