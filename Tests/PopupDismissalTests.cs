using ChargeKeeper.Helpers;
using Xunit;
using ZeroZero.Win32;

namespace ChargeKeeper.Tests;

/// <summary>
/// Four windows close themselves when they lose focus. The update component's window takes focus as
/// it opens and owns none of them, so an unguarded popup closes the instant an update appears over
/// it — and the About window, which owns the update it asked for, takes that update down with it.
/// That is a defect a person meets, on the one path where losing the window loses the update.
/// </summary>
public class PopupDismissalTests
{
    /// <summary>Every window that dismisses on deactivation. A new one belongs on this list.</summary>
    public static TheoryData<string> DismissingWindows()
    {
        var data = new TheoryData<string>();
        data.Add("AboutWindow.xaml.cs");
        data.Add("BatteryHistoryWindow.xaml.cs");
        data.Add("DashboardWindow.xaml.cs");
        data.Add("FocusStartWindow.xaml.cs");
        return data;
    }

    private static string ActivationHandler(string fileName) =>
        SourceMethods.Body(File.ReadAllText(RepoFiles.Find(Path.Combine("UI", fileName))), "OnActivated");

    // None of these windows can be instantiated without a display, so the wiring is read from the
    // shipped source.
    [Theory]
    [MemberData(nameof(DismissingWindows))]
    public void AWindowThatDismissesOnFocusLossAsksBeforeClosing(string fileName)
    {
        string body = ActivationHandler(fileName);

        Assert.Contains("WindowChrome.DismissalHeld", body, StringComparison.Ordinal);
        Assert.Contains("Deactivated", body, StringComparison.Ordinal);
    }

    // A guard consulted after the window has already closed itself is no guard. The check has to
    // stand between the deactivation test and whatever closes the window.
    [Theory]
    [MemberData(nameof(DismissingWindows))]
    public void TheGuardStandsBeforeTheDismissal(string fileName)
    {
        string body = ActivationHandler(fileName);

        int guard    = body.IndexOf("WindowChrome.DismissalHeld", StringComparison.Ordinal);
        int deactivated = body.IndexOf("Deactivated", StringComparison.Ordinal);
        int closes   = body.IndexOf("Dismiss()", StringComparison.Ordinal);
        if (closes < 0) closes = body.IndexOf("HideWindow()", StringComparison.Ordinal);

        Assert.True(closes > 0, $"{fileName} no longer closes itself in OnActivated.");
        Assert.InRange(guard, deactivated, closes);
    }

    // The hold is the component's own scope, entered by its window's constructor and left when that
    // window closes. Read through the same property the windows consult, so a property rewired to
    // something else fails here.
    [Fact]
    public void TheHoldIsOnForExactlyAsLongAsATransientWindow()
    {
        Assert.False(WindowChrome.DismissalHeld);

        using (TransientWindows.Enter())
            Assert.True(WindowChrome.DismissalHeld);

        Assert.False(WindowChrome.DismissalHeld);
    }

    // Two update windows cannot be up at once today, but the scope counts rather than latches, and a
    // guard that read "any" as "the last one" would release the hold too early.
    [Fact]
    public void NestedHoldsReleaseOnlyWhenTheLastOneGoes()
    {
        var outer = TransientWindows.Enter();
        var inner = TransientWindows.Enter();

        inner.Dispose();
        Assert.True(WindowChrome.DismissalHeld);

        outer.Dispose();
        Assert.False(WindowChrome.DismissalHeld);
    }
}
