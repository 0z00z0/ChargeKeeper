using System.Text.RegularExpressions;
using Xunit;

namespace ChargeKeeper.Tests;

// The dashboard's badges each carry a description line beside a toggle switch. The line sits in a
// star column, so a TextBlock that sets neither wrapping nor trimming reports a desired width the
// column cannot give it and the text runs on underneath the switch — clipped, at any window width.
// These assertions read the markup, so they hold without a display.
public class DashboardBadgeLayoutTests
{
    private static string DashboardMarkup() =>
        File.ReadAllText(RepoFiles.Find(Path.Combine("UI", "DashboardWindow.xaml")));

    /// <summary>Every badge's description line, by the x:Name the window writes to.</summary>
    private static readonly string[] DetailLines =
    [
        "SmartChargeDetailText", "SmartStandbyDetailText", "LidDelayDetailText", "KeepAwakeDetailText",
    ];

    /// <summary>The markup of one element, from its x:Name to the end of its tag.</summary>
    private static string Element(string name)
    {
        string xaml  = DashboardMarkup();
        int    start = xaml.IndexOf($"x:Name=\"{name}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{name} is no longer declared in DashboardWindow.xaml.");

        int end = xaml.IndexOf("/>", start, StringComparison.Ordinal);
        Assert.True(end > start, $"{name} is no longer a self-closing element.");
        return xaml[start..end];
    }

    [Theory]
    [InlineData("SmartChargeDetailText")]
    [InlineData("SmartStandbyDetailText")]
    [InlineData("LidDelayDetailText")]
    [InlineData("KeepAwakeDetailText")]
    public void EveryBadgeDescriptionConstrainsItsOwnWidth(string name)
    {
        // Width alone never fixes this: a long enough string overflows whatever the window measures
        // to, so the constraint has to be on the line itself.
        string element = Element(name);
        Assert.Contains("TextWrapping=\"Wrap\"", element, StringComparison.Ordinal);
        Assert.Contains("TextTrimming=\"CharacterEllipsis\"", element, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryBadgeDescriptionIsBoundedInHeightToo()
    {
        // Wrapping without a line cap trades a clipped line for a badge that grows without limit in
        // a popup measured from its content.
        foreach (string name in DetailLines)
            Assert.Matches(new Regex(@"MaxLines=""[1-9]\d*"""), Element(name));
    }

    [Fact]
    public void TheBadgeDescriptionsAreTheOnlyOnesThisRuleCovers()
    {
        // A fifth badge added without a description line would otherwise pass by never being looked
        // at, since the theory above only names the four that exist.
        int declared = Regex.Matches(DashboardMarkup(), @"x:Name=""\w+DetailText""").Count;
        Assert.Equal(DetailLines.Length, declared);
    }
}
