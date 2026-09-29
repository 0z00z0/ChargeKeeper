using System;
using System.Linq;
using System.Text.Json;
using ChargeKeeper.Services;
using Xunit;
using ZeroZero.Primitives;
using ZeroZero.Update.Win32;

namespace ChargeKeeper.Tests;

/// <summary>
/// The two General-page update settings: what the document stores them as, what an installed
/// document that predates them reads as, what the shared policy runs on for each cadence, and what
/// refuses an automatic install.
/// </summary>
public class UpdateScheduleTests
{
    // ── What is on disk ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheStoredCadences_AreTheNamesEveryInstallationWillHave()
    {
        // The settings document stores the member name, so a renamed or reordered member resets
        // every installation's choice. The XAML carries the same three strings as item tags.
        Assert.Equal(["EveryHour", "EveryDay", "AtStartupOnly"], Enum.GetNames<UpdateCheckCadence>());
        Assert.Equal(["\"EveryHour\"", "\"EveryDay\"", "\"AtStartupOnly\""],
                     Enum.GetValues<UpdateCheckCadence>().Select(c => JsonSerializer.Serialize(c)));
    }

    [Fact]
    public void TheSettingsPageOffersTheThreeStoredNames()
    {
        // The combo commits its item's Tag by name, so a tag naming no member silently commits
        // nothing and the row looks like it does not work.
        string xaml  = File.ReadAllText(RepoFiles.Find(Path.Combine("UI", "SettingsWindow.xaml")));
        int    start = xaml.IndexOf("x:Name=\"UpdateCadenceCombo\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "UpdateCadenceCombo is no longer declared in SettingsWindow.xaml.");
        int end = xaml.IndexOf("</ComboBox>", start, StringComparison.Ordinal);

        var tags = System.Text.RegularExpressions.Regex
            .Matches(xaml[start..end], @"Tag=""(?<tag>[^""]*)""")
            .Select(m => m.Groups["tag"].Value);

        Assert.Equal(Enum.GetNames<UpdateCheckCadence>(), tags);
    }

    [Fact]
    public void ADocumentWrittenBeforeTheRowsExisted_ChecksDailyAndInstallsNothing()
    {
        // Both keys are nullable for this: a plain enum would read as its first member, which is
        // hourly, and a plain bool would switch installing without asking on by accident.
        var general = JsonSerializer.Deserialize<SettingsFile.GeneralGroup>(
            """{"StartupDelaySeconds":10,"IconMode":"Arc","PromoteTrayIcons":false,"LastSeenVersion":"1.0.0"}""")!;

        Assert.Null(general.UpdateCheckCadence);
        Assert.Null(general.InstallUpdatesAutomatically);

        var settings = new SettingsFile { General = general }.ToSettings();

        Assert.Equal(UpdateCheckCadence.EveryDay, settings.UpdateCheckCadence);
        Assert.False(settings.InstallUpdatesAutomatically);
    }

    // ── What the policy runs on ─────────────────────────────────────────────────────────────────

    private static UnattendedUpdateOptions Options(string cadence) =>
        UpdateSchedulePolicy.Options(Enum.Parse<UpdateCheckCadence>(cadence), shutdown: () => { },
                                     mayInstallNow: _ => InstallMoment.Now, tickReported: _ => { },
                                     log: NullLogSink.Instance);

    // The cadence arrives by name: the enum is internal, so it cannot be a public parameter, and
    // naming it here doubles as a second reading of the stored spelling.
    [Theory]
    [InlineData("EveryDay", CheckCadence.Periodic, 24)]
    public void ARepeatingCadenceChecksAtItsOwnGap(string cadence, CheckCadence expected, int hours)
    {
        var options = Options(cadence);
        Assert.Equal(expected, options.Cadence);
        Assert.Equal(TimeSpan.FromHours(hours), options.CheckInterval);
    }

    [Theory]
    [InlineData("EveryDay")]
    public void EveryCadenceRunsThePolicy_ThirtySecondsAfterStart(string cadence)
    {
        // Enabled whatever the Settings switch says: the policy's check is what keeps the tray line
        // current, and an installation with installing switched off still has to learn of a release.
        var options = Options(cadence);
        Assert.True(options.Enabled);
        Assert.Equal(TimeSpan.FromSeconds(30), options.InitialDelay);
    }

    // ── What refuses an automatic install ───────────────────────────────────────────────────────

    [Fact]
    public void TheSwitchOff_RefusesEvenWithEverythingElseClear()
    {
        var moment = UpdateSchedulePolicy.MayInstallNow(installAutomatically: false, false, false);
        Assert.False(moment.Accepted);
        Assert.Equal("installing automatically is switched off", moment.Reason);
    }

    [Fact]
    public void ALidCloseWait_RefusesTheInstall()
    {
        // The lid is shut, so the machine reads as free exactly when it is on its way to sleep —
        // the component's own idle rule would let the install through.
        var moment = UpdateSchedulePolicy.MayInstallNow(true, focusRunning: false, lidWaitRunning: true);
        Assert.False(moment.Accepted);
        Assert.Equal("a lid-close wait is running", moment.Reason);
    }
}
