using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace ChargeKeeper.Tests;

// The Smart Charge, Keep Awake and Lid close pages carry one layout: a section opens with a rule
// and a sub-heading, and the cards follow. The shape only holds while every page draws that chrome
// from SettingsSectionHeader — a page that hand-rolls a divider and a heading looks right on the
// day it is written and drifts afterwards. These assertions read the markup, so they hold without
// a display.
public class SettingsSectionLayoutTests
{
    private static string SettingsMarkup() =>
        File.ReadAllText(RepoFiles.Find(Path.Combine("UI", "SettingsWindow.xaml")));

    private static string MarkupDirectory() =>
        Path.GetDirectoryName(RepoFiles.Find(Path.Combine("UI", "SettingsWindow.xaml")))!;

    /// <summary>The window's pages, in markup order. A page ends where the next one is declared.</summary>
    private static readonly string[] Pages =
    [
        "GeneralPanel", "SmartChargePanel", "KeepAwakePanel", "LidClosePanel",
        "NotificationsPanel", "HomeAssistantPanel", "AboutPanel",
    ];

    /// <summary>The markup of one page panel.</summary>
    private static string Page(string panelName)
    {
        string xaml  = SettingsMarkup();
        int    index = Array.IndexOf(Pages, panelName);
        Assert.True(index >= 0, $"{panelName} is not one of the window's pages.");

        int start = xaml.IndexOf($"<StackPanel x:Name=\"{panelName}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{panelName} is no longer declared in SettingsWindow.xaml.");

        if (index + 1 == Pages.Length) return xaml[start..];

        int end = xaml.IndexOf($"<StackPanel x:Name=\"{Pages[index + 1]}\"", start, StringComparison.Ordinal);
        Assert.True(end > start, $"{Pages[index + 1]} no longer follows {panelName}.");
        return xaml[start..end];
    }

    private static string[] SectionHeadings(string panelName) =>
        Regex.Matches(Page(panelName), @"<local:SettingsSectionHeader\s+Heading=""(?<heading>[^""]*)""")
             .Select(m => m.Groups["heading"].Value)
             .ToArray();

    [Theory]
    [InlineData("GeneralPanel",     new[] { "Advanced" })]
    [InlineData("SmartChargePanel", new[] { "Charge limit", "Presets", "Network profiles" })]
    [InlineData("KeepAwakePanel",   new[] { "Presets", "Networks" })]
    [InlineData("LidClosePanel",    new[] { "Sleep delay", "Battery target", "Locking" })]
    public void EverySectionOpensWithTheSharedHeader(string panelName, string[] headings) =>
        Assert.Equal(headings, SectionHeadings(panelName));

    [Fact]
    public void TheSectionStylesAreDrawnFromOnePlaceOnly()
    {
        string[] users = Directory.EnumerateFiles(MarkupDirectory(), "*.xaml")
                                  .Where(f => File.ReadAllText(f).Contains("SectionDividerStyle",
                                                                           StringComparison.Ordinal)
                                           || File.ReadAllText(f).Contains("SubHeaderStyle",
                                                                           StringComparison.Ordinal))
                                  .Select(Path.GetFileName)
                                  .ToArray()!;

        Assert.Equal(["SettingsSectionHeader.xaml"], users);
    }

    // The two pages describe the same network from the same service. Declared twice, the copies
    // drifted in wording once already.

    [Fact]
    public void TheCurrentNetworkRowIsDeclaredOnce()
    {
        Assert.DoesNotContain("Header=\"Current network\"", SettingsMarkup(), StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(SettingsMarkup(), "<local:CurrentNetworkCard").Count);
    }

    [Fact]
    public void TheAddProfileLabelIsWrittenOnce()
    {
        string xaml = SettingsMarkup();
        Assert.Equal(1, Regex.Matches(xaml, @"x:Key=""AddNetworkProfileLabel""").Count);
        Assert.Equal(2, Regex.Matches(xaml, @"\{StaticResource AddNetworkProfileLabel\}").Count);
    }
}
