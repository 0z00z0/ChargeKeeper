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
        Assert.NotEmpty(Source(Path.Combine("Services", "ScreenCoverService.cs")));
    }

    [Fact]
    public void TheInputBlockLapsesRatherThanPersisting() =>
        // The whole safety of the input lever: the thread holding the block gives it up unless
        // something keeps pushing a deadline forward, so the application hanging cannot leave a
        // machine nobody can type on. A block that waited to be told to stop could.
        Assert.Matches(new Regex(@"DateTimeOffset\.UtcNow - _renewedAt > RenewalWindow"),
                       Source(Path.Combine("Services", "InputBlock.cs")));

    [Fact]
    public void TheInputBlockIsReleasedWhenTheApplicationCloses() =>
        // Not left to what ending the process does to a block, which cannot be measured from
        // inside it.
        Assert.Contains("InputBlock.Release(\"the application is closing\")",
                        Source(Path.Combine("Services", "FocusSessionService.cs")),
                        StringComparison.Ordinal);

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

    [Fact]
    public void TheCoverRefusesEveryCloseRequest() =>
        // Alt+F4, the switcher's close and an ordinary End task all reach the window as a close
        // request. Without this the cover is one keystroke away from gone, with the session still
        // running and nothing on screen saying so.
        Assert.Contains("NativeMethods.RefuseClose(",
                        Source(Path.Combine("UI", "ScreenCoverWindow.xaml.cs")),
                        StringComparison.Ordinal);

    [Fact]
    public void TheOneCloseThatWorksLiftsTheRefusalFirst() =>
        // The session's own teardown and a rebuild both go through Dismiss, which allows the close
        // before asking for it. A teardown that did not would leave a cover nothing can take down.
        Assert.Matches(new Regex(@"_refusal\?\.Allow\(\);\s*Close\(\);"),
                       Source(Path.Combine("UI", "ScreenCoverWindow.xaml.cs")));

    [Fact]
    public void ACoverThatHasGoneIsPutBackByTheTick() =>
        // The second defence, and the one that does not depend on knowing how a cover went away.
        // The reading has to drive the rebuild: naming the method anywhere would also be satisfied
        // by its own declaration, which is how this guard first survived having its call deleted.
        Assert.Matches(new Regex(@"if \(AnyCoverIsGone\(\)\)[\s\S]{0,160}?Rebuild\(\);"),
                       Source(Path.Combine("Services", "ScreenCoverService.cs")));

    [Fact]
    public void TheCoverAssertsItsStylesOnEveryTick() =>
        // A cover that could take focus is a cover that can be closed and typed at. Re-asserting
        // costs nothing and corrects a style anything else put back within the same second.
        Assert.Matches(new Regex(@"KeepOnTop\(\)\s*\{[^}]*MakeClickThroughAndUnfocusable"),
                       Source(Path.Combine("UI", "ScreenCoverWindow.xaml.cs")));

    [Theory]
    [InlineData("_focusLeverActions.SetFocusBlocksNetwork(")]
    [InlineData("_focusLeverActions.SetFocusDimsScreen(")]
    [InlineData("_focusLeverActions.SetFocusCoversScreen(")]
    [InlineData("_focusLeverActions.SetFocusBlocksInput(")]
    public void TheSettingsPageLeverSwitchesWriteThroughTheSharedAction(string call) =>
        // The same action an inbound MQTT command uses, so a lever changed from the Settings page
        // gets the same refusal while a session runs rather than a second, unguarded write path
        // straight to the settings document.
        Assert.Contains(call, Source(Path.Combine("UI", "SettingsWindow.xaml.cs")), StringComparison.Ordinal);
}
