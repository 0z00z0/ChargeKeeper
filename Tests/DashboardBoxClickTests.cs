using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// #206: only a badge's own switch and its chevron change anything. A click anywhere else in the
/// box — the title, the description line, the collapsed row's text — must do nothing, so none of
/// those elements may carry a Tapped handler; only the chevron host may. Read from markup, like
/// <see cref="DashboardBadgeLayoutTests"/>, since WinUI cannot be driven without a display.
/// </summary>
public class DashboardBoxClickTests
{
    private static string DashboardMarkup() =>
        File.ReadAllText(RepoFiles.Find(Path.Combine("UI", "DashboardWindow.xaml")));

    /// <summary>The header shown while a badge is expanded (title, description, tooltip).</summary>
    private static readonly string[] ExpandedHeaders =
    [
        "SmartChargeExpandedHeader", "SmartStandbyExpandedHeader", "LidDelayExpandedHeader", "KeepAwakeExpandedHeader",
    ];

    /// <summary>The dense row shown instead, while "One line until it matters" has collapsed the badge.</summary>
    private static readonly string[] CollapsedHeaders =
    [
        "SmartChargeCollapsedHeader", "SmartStandbyCollapsedHeader", "LidDelayCollapsedHeader", "KeepAwakeCollapsedHeader",
    ];

    /// <summary>The chevron beside each header — the only element that may expand or collapse it.</summary>
    private static readonly string[] ChevronHosts =
    [
        "SmartChargeChevronHost", "SmartStandbyChevronHost", "LidDelayChevronHost", "KeepAwakeChevronHost",
    ];

    /// <summary>The opening tag of one element, from its own "&lt;" to its own "&gt;" — never as far
    /// as a matching close tag, since a StackPanel's children would otherwise hide a Tapped attribute
    /// belonging to a child rather than to the panel itself.</summary>
    private static string OpeningTag(string name)
    {
        string xaml = DashboardMarkup();
        int nameAt  = xaml.IndexOf($"x:Name=\"{name}\"", StringComparison.Ordinal);
        Assert.True(nameAt >= 0, $"{name} is no longer declared in DashboardWindow.xaml.");

        int start = xaml.LastIndexOf('<', nameAt);
        int end   = xaml.IndexOf('>', nameAt);
        Assert.True(start >= 0 && end > start, $"{name}'s opening tag could not be isolated.");
        return xaml[start..(end + 1)];
    }

    [Theory]
    [MemberData(nameof(ExpandedAndCollapsedHeaders))]
    public void HeaderTextCarriesNoTappedHandler(string name) =>
        Assert.DoesNotContain("Tapped=", OpeningTag(name), StringComparison.Ordinal);

    [Theory]
    [InlineData("SmartChargeChevronHost")]
    [InlineData("SmartStandbyChevronHost")]
    [InlineData("LidDelayChevronHost")]
    [InlineData("KeepAwakeChevronHost")]
    public void OnlyTheChevronExpandsOrCollapsesTheBox(string name) =>
        Assert.Contains("Tapped=", OpeningTag(name), StringComparison.Ordinal);

    /// <summary>Guards the guard: every header and every chevron this class knows about is still
    /// declared, so a rename that dropped an element silently would not just pass by omission.</summary>
    [Fact]
    public void EveryBadgeIsCoveredByName()
    {
        foreach (string name in ExpandedHeaders) OpeningTag(name);
        foreach (string name in CollapsedHeaders) OpeningTag(name);
        foreach (string name in ChevronHosts) OpeningTag(name);
    }

    public static IEnumerable<object[]> ExpandedAndCollapsedHeaders() =>
        ExpandedHeaders.Concat(CollapsedHeaders).Select(name => new object[] { name });
}
