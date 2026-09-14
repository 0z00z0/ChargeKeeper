using Xunit;

namespace ChargeKeeper.Tests;

// Escape closes the dashboard, the pop-out graph and the About window. What "closes" means differs
// and must keep differing: the dashboard hides, so the tray's next click re-shows the same window,
// and the other two are destroyed, because their owners recreate them. A window whose Escape took
// another path would be a behaviour nothing else in the app has. None of the windows can be
// instantiated without a display, so these read the shipped markup and code-behind.
public class WindowEscapeKeyTests
{
    private static string Markup(string fileName) =>
        File.ReadAllText(RepoFiles.Find(Path.Combine("UI", fileName)));

    /// <summary>The body of a method in a code-behind, from its signature to the next one.</summary>
    private static string MethodBody(string fileName, string signatureFragment)
    {
        string source = Markup(fileName);
        int start = source.IndexOf(signatureFragment, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signatureFragment} is no longer declared in {fileName}.");

        int open = source.IndexOf('{', start);
        Assert.True(open > start, $"{signatureFragment} in {fileName} has no body.");

        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }

        Assert.Fail($"{signatureFragment} in {fileName} has an unbalanced body.");
        return string.Empty;
    }

    [Theory]
    [InlineData("DashboardWindow.xaml")]
    [InlineData("BatteryHistoryWindow.xaml")]
    [InlineData("AboutWindow.xaml")]
    public void WindowBindsEscapeOnItsRoot(string fileName)
    {
        string xaml = Markup(fileName);

        Assert.Contains("<Grid.KeyboardAccelerators>", xaml, StringComparison.Ordinal);
        Assert.Contains("Key=\"Escape\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Invoked=\"OnEscapeInvoked\"", xaml, StringComparison.Ordinal);
    }

    // WinUI shows an accelerator's key as a tooltip on the element carrying it, and on everything
    // inside that has no tooltip of its own — so a root accelerator put "Esc" over the whole window.
    [Theory]
    [InlineData("DashboardWindow.xaml")]
    [InlineData("BatteryHistoryWindow.xaml")]
    [InlineData("AboutWindow.xaml")]
    public void WindowHidesTheEscapeKeyTip(string fileName)
    {
        string xaml = Markup(fileName);
        var root = System.Text.RegularExpressions.Regex.Match(xaml, @"<Grid\s+x:Name=""RootGrid""[^>]*>");

        Assert.True(root.Success, $"{fileName} no longer declares its RootGrid.");
        Assert.Contains("KeyboardAcceleratorPlacementMode=\"Hidden\"", root.Value, StringComparison.Ordinal);
    }

    // Left unhandled, the key keeps bubbling and a second accelerator could act on the same press.
    [Theory]
    [InlineData("DashboardWindow.xaml.cs")]
    [InlineData("BatteryHistoryWindow.xaml.cs")]
    [InlineData("AboutWindow.xaml.cs")]
    public void EscapeHandlerMarksTheKeyHandled(string fileName) =>
        Assert.Contains("args.Handled = true", MethodBody(fileName, "void OnEscapeInvoked"),
                        StringComparison.Ordinal);

    [Theory]
    [InlineData("DashboardWindow.xaml.cs")]
    [InlineData("BatteryHistoryWindow.xaml.cs")]
    [InlineData("AboutWindow.xaml.cs")]
    public void EscapeHandlerExistsInTheCodeBehind(string fileName) =>
        Assert.Contains("OnEscapeInvoked", Markup(fileName), StringComparison.Ordinal);

    // Hide, not close: the idle timer reclaims the window later, and the tray re-shows this one.
    [Fact]
    public void DashboardEscapeHides()
    {
        string body = MethodBody("DashboardWindow.xaml.cs", "void OnEscapeInvoked");

        Assert.Contains("HideWindow()", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Close()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void DashboardHideDoesNotDestroyTheWindow()
    {
        string body = MethodBody("DashboardWindow.xaml.cs", "public void HideWindow()");

        Assert.Contains("AppWindow.Hide()", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Close()", body, StringComparison.Ordinal);
    }

    // The same dismissal clicking away takes, so Escape introduces no third behaviour.
    [Fact]
    public void HistoryWindowEscapeTakesTheClickAwayPath()
    {
        string body = MethodBody("BatteryHistoryWindow.xaml.cs", "void OnEscapeInvoked");

        Assert.Contains("Dismiss()", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Hide()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void HistoryWindowDismissDestroysTheWindow()
    {
        string body = MethodBody("BatteryHistoryWindow.xaml.cs", "private void Dismiss()");

        Assert.Contains("Close", body, StringComparison.Ordinal);
        Assert.DoesNotContain("AppWindow.Hide()", body, StringComparison.Ordinal);
    }

    // Focus loss and Escape must stay one path; a second copy of the retract-then-close sequence
    // would drift from this one.
    [Fact]
    public void HistoryWindowFocusLossUsesTheSameDismissal() =>
        Assert.Contains("Dismiss()", MethodBody("BatteryHistoryWindow.xaml.cs", "void OnActivated"),
                        StringComparison.Ordinal);

    // The About window follows the pop-out: frameless, so Escape and clicking away are the only ways
    // out, and both must reach the one dismissal that closes it.
    [Fact]
    public void AboutWindowIsAFramelessPopupWithOneDismissal()
    {
        Assert.Contains("WindowChrome.ApplyPopup", Markup("AboutWindow.xaml.cs"), StringComparison.Ordinal);
        Assert.Contains("Dismiss()", MethodBody("AboutWindow.xaml.cs", "void OnEscapeInvoked"),
                        StringComparison.Ordinal);
        Assert.Contains("Dismiss()", MethodBody("AboutWindow.xaml.cs", "void OnActivated"),
                        StringComparison.Ordinal);
        Assert.Contains("Close()", MethodBody("AboutWindow.xaml.cs", "private void Dismiss()"),
                        StringComparison.Ordinal);
    }

    // Escape would be stolen mid-edit by a text box, and would close a dropdown or a dialogue rather
    // than the window. Neither window has any, which is why the accelerator can sit on the root.
    [Theory]
    [InlineData("DashboardWindow.xaml")]
    [InlineData("BatteryHistoryWindow.xaml")]
    [InlineData("BatteryHistoryGraphControl.xaml")]
    public void WindowHasNothingThatWantsEscapeForItself(string fileName)
    {
        string xaml = Markup(fileName);

        foreach (string control in new[] { "<TextBox", "<ComboBox", "<AutoSuggestBox", "<ContentDialog", "Flyout=", "<Popup" })
            Assert.DoesNotContain(control, xaml, StringComparison.Ordinal);
    }
}
