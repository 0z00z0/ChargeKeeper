using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

// A running keep-awake session suppresses a lid-close sleep. The rule is one rule, and both
// features' pages have to state it — the defect #172 records is that only the lid side did.
public class KeepAwakeLidCouplingTests
{
    private static string SettingsMarkup() =>
        File.ReadAllText(RepoFiles.Find(Path.Combine("UI", "SettingsWindow.xaml")));

    /// <summary>The markup between one page panel's declaration and the next page's.</summary>
    private static string Page(string panelName, string nextPanelName)
    {
        string xaml = SettingsMarkup();
        int start = xaml.IndexOf($"<StackPanel x:Name=\"{panelName}\"", StringComparison.Ordinal);
        int end   = xaml.IndexOf($"<StackPanel x:Name=\"{nextPanelName}\"", start + 1, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, $"{panelName} no longer precedes {nextPanelName}.");
        return xaml[start..end];
    }

    // Guards on both directions. Neither page may lose the rule silently: a page that stops stating
    // it looks exactly the same as one that never had it, which is the state #172 was filed against.
    // #221 shortened both pages' help text and dropped the phrase the two sides used to share, so
    // each page now states the rule in its own words rather than an identical shared sentence.

    [Fact]
    public void KeepAwakePage_StatesWhatASessionDoesToALidClose() =>
        Assert.Contains("holds off any lid-close sleep",
                        Page("KeepAwakePanel", "LidClosePanel"), StringComparison.Ordinal);

    [Fact]
    public void LidDelayPage_StatesThatAKeepAwakeSessionHoldsOffTheSleep() =>
        Assert.Contains("keep-awake session holding off the sleep",
                        Page("LidClosePanel", "NotificationsPanel"), StringComparison.Ordinal);

    // DescribeLidEffect

    [Fact]
    public void DescribeLidEffect_SessionRunningWithLidHandlingOn_SaysSo() =>
        Assert.Equal("A lid close will not sleep this computer while this session lasts.",
                     KeepAwakePolicy.DescribeLidEffect(sessionRunning: true, lidDelayEnabled: true));

    [Fact]
    public void DescribeLidEffect_ScopesTheClaimToThisSession() =>
        // Unqualified, the sentence would outlive the session that causes it — and would be false
        // the moment a suppressed lid close is completed when the session ends.
        Assert.Contains("while this session lasts",
                        KeepAwakePolicy.DescribeLidEffect(true, true)!, StringComparison.Ordinal);

    [Fact]
    public void DescribeLidEffect_NoSession_SaysNothing() =>
        Assert.Null(KeepAwakePolicy.DescribeLidEffect(sessionRunning: false, lidDelayEnabled: true));

    [Fact]
    public void DescribeLidEffect_LidHandlingOff_SaysNothing() =>
        // Windows' own lid-close action is in charge, and a session does not suppress that.
        Assert.Null(KeepAwakePolicy.DescribeLidEffect(sessionRunning: true, lidDelayEnabled: false));
}
