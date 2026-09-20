using System.Text.RegularExpressions;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The feature's central property, held against the source: a person at the keyboard can start a
/// focus session and cannot end one. Ending is Home Assistant's, after a five-minute wait and a
/// second request.
/// </summary>
/// <remarks>Source text rather than behaviour, like the dashboard's other structural guards: the
/// windows are WinUI code-behind that cannot be driven without a display, and what must never
/// appear there is a call, not a value.</remarks>
public class FocusNoLocalWayOutTests
{
    /// <summary>Every window a person can reach from the machine itself.</summary>
    private static readonly string[] LocalSurfaces =
    [
        Path.Combine("UI", "DashboardWindow.xaml.cs"),
        Path.Combine("UI", "FocusStartWindow.xaml.cs"),
        Path.Combine("UI", "SettingsWindow.xaml.cs"),
        Path.Combine("UI", "TrayMenu.cs"),
        Path.Combine("UI", "ScreenCoverWindow.xaml.cs"),
    ];

    private static string Source(string relativePath) =>
        File.ReadAllText(RepoFiles.Find(relativePath));

    [Theory]
    [InlineData("UI/DashboardWindow.xaml.cs")]
    [InlineData("UI/FocusStartWindow.xaml.cs")]
    [InlineData("UI/SettingsWindow.xaml.cs")]
    [InlineData("UI/TrayMenu.cs")]
    [InlineData("UI/ScreenCoverWindow.xaml.cs")]
    public void NoSurfaceOnTheMachineAsksToCancelASession(string relativePath)
    {
        // RequestCancel is the only route to ending a session, and it belongs to the MQTT command
        // seam alone. A button added here would hand the keyboard the way out the feature exists to
        // refuse.
        string source = Source(relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.DoesNotContain("RequestCancel", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGuardIsLookingAtWindowsThatExist()
    {
        // Guards the guard: a renamed or deleted window would otherwise pass by never being read.
        foreach (string path in LocalSurfaces) Assert.NotEmpty(Source(path));
    }

    [Fact]
    public void OnlyTheStartBoxArmsASessionFromTheMachine()
    {
        // The dashboard opens the box; the box is what arms. A dashboard button wired straight to
        // Arm would start a session with whatever length happened to be stored and no warning
        // about what a session costs.
        Assert.DoesNotContain("FocusSessionService.Arm",
                              Source(Path.Combine("UI", "DashboardWindow.xaml.cs")),
                              StringComparison.Ordinal);
        Assert.Contains("FocusSessionService.Arm",
                        Source(Path.Combine("UI", "FocusStartWindow.xaml.cs")),
                        StringComparison.Ordinal);
    }

    [Fact]
    public void TheStartBoxHandsItsOwnLengthToTheSession() =>
        // The length chosen in the box reaches the session rather than being written down and
        // forgotten. Without the argument the session would silently run for the stored default.
        Assert.Matches(new Regex(@"FocusSessionService\.Arm\([^)]*chosen\)"),
                       Source(Path.Combine("UI", "FocusStartWindow.xaml.cs")));

    [Fact]
    public void TheDashboardsStartButtonFollowsItsSetting() =>
        // The setting is what takes the button away. A button shown regardless would make the
        // setting a line in a document and nothing else.
        Assert.Contains("FocusStartFromDashboard",
                        Source(Path.Combine("UI", "DashboardWindow.xaml.cs")),
                        StringComparison.Ordinal);
}
