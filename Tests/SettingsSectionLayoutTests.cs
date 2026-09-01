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
    [InlineData("LidClosePanel",    new[] { "Sleep after a time", "Sleep at a battery level" })]
    public void EverySectionOpensWithTheSharedHeader(string panelName, string[] headings) =>
        Assert.Equal(headings, SectionHeadings(panelName));

    /// <summary>The Lid close master switch governs the whole page, so no section heading may stand
    /// above it: a heading over it reads as though it belonged to that one section.</summary>
    [Fact]
    public void TheLidCloseMasterSwitchSitsAboveEverySectionHeading()
    {
        string page   = Page("LidClosePanel");
        int    master = page.IndexOf("x:Name=\"LidDelayToggle\"", StringComparison.Ordinal);
        int    first  = page.IndexOf("<local:SettingsSectionHeader", StringComparison.Ordinal);

        Assert.True(master >= 0, "The Lid close master switch is no longer declared.");
        Assert.True(first  >= 0, "The Lid close page no longer has any section heading.");
        Assert.True(master < first, "A section heading stands above the Lid close master switch.");
    }

    /// <summary>The two rows that apply to either kind of wait come before either preset group, so
    /// neither group can read as owning them.</summary>
    [Fact]
    public void TheLidCloseSharedRowsComeBeforeBothPresetGroups()
    {
        string page = Page("LidClosePanel");
        int offAfterSleep = page.IndexOf("x:Name=\"LidOffAfterSleepToggle\"", StringComparison.Ordinal);
        int lockOnClose   = page.IndexOf("x:Name=\"LidLockToggle\"", StringComparison.Ordinal);
        int firstHeading  = page.IndexOf("<local:SettingsSectionHeader", StringComparison.Ordinal);

        Assert.True(offAfterSleep >= 0 && lockOnClose >= 0);
        Assert.True(offAfterSleep < firstHeading, "Switching off after sleeping fell inside a preset group.");
        Assert.True(lockOnClose   < firstHeading, "Locking on lid close fell inside a preset group.");
    }

    /// <summary>Each preset group carries its own list panel and its own add button — split rather
    /// than one merged list.</summary>
    [Fact]
    public void TheLidClosePresetGroupsAreSplit()
    {
        string page = Page("LidClosePanel");
        foreach (string name in new[]
                 {
                     "LidDelayPresetsListPanel", "AddLidDelayPresetBtn",
                     "LidDischargeTargetsListPanel", "AddLidDischargeTargetBtn",
                 })
            Assert.Contains($"x:Name=\"{name}\"", page, StringComparison.Ordinal);
    }

    /// <summary>The markup of the SettingsCard whose content declares <paramref name="toggleName"/>.</summary>
    private static string CardHolding(string panelName, string toggleName)
    {
        string page   = Page(panelName);
        int    toggle = page.IndexOf($"x:Name=\"{toggleName}\"", StringComparison.Ordinal);
        Assert.True(toggle >= 0, $"{toggleName} is no longer declared on {panelName}.");

        int start = page.LastIndexOf("<controls:SettingsCard", toggle, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{toggleName} does not sit inside a SettingsCard.");

        int end = page.IndexOf("</controls:SettingsCard>", start, StringComparison.Ordinal);
        Assert.True(end > start, $"the SettingsCard holding {toggleName} is not closed.");
        return page[start..end];
    }

    private static string DescriptionOf(string panelName, string toggleName)
    {
        var match = Regex.Match(CardHolding(panelName, toggleName), @"Description=""(?<text>[^""]*)""");
        Assert.True(match.Success, $"the card holding {toggleName} carries no Description.");
        return match.Groups["text"].Value;
    }

    // Lid close ends its wait on whichever condition arrives first. Both group switches once
    // described their value as "one of the conditions for sleeping", which reads as a conjunction —
    // the behaviour the redesign removed — and contradicted the master switch's own bubble one level
    // up. The wording is the only place a user learns the rule, so it is asserted rather than left to
    // review.

    [Theory]
    [InlineData("LidDelayTimeToggle",  "battery target is reached first")]
    [InlineData("LidDischargeToggle",  "delay runs out first")]
    public void EachLidClosePresetGroupSwitchNamesTheRaceItLosesTo(string toggle, string race)
    {
        string description = DescriptionOf("LidClosePanel", toggle);

        Assert.Contains(race, description, StringComparison.Ordinal);
        Assert.DoesNotContain("one of the conditions", description, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>No user-visible string anywhere may describe a lid-close condition as one of several
    /// that have to hold. The phrasing survived one sweep already by sitting on a second card.</summary>
    [Fact]
    public void NoLidCloseStringDescribesItsConditionsAsAConjunction() =>
        Assert.DoesNotContain("one of the conditions for", SettingsMarkup(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Every switch in the Lid close top block carries an info bubble. Each of the three
    /// turns on behaviour whose reason does not fit the one-line description — the master switch
    /// changes a Windows setting, switching off after sleeping counts only sleeps, and locking exists
    /// because handling the lid removes the sign-in prompt. A row without one strands its reason in a
    /// source comment no user reads.</summary>
    [Theory]
    [InlineData("LidDelayToggle")]
    [InlineData("LidOffAfterSleepToggle")]
    [InlineData("LidLockToggle")]
    public void EveryLidCloseTopBlockSwitchCarriesAnInfoBubble(string toggle) =>
        Assert.Contains("<local:InfoIcon", CardHolding("LidClosePanel", toggle), StringComparison.Ordinal);

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
