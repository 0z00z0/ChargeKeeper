using System;
using System.Linq;
using System.Text.Json;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The two General-page update settings: what the document stores them as, what an installed
/// document that predates them reads as, when a tick asks GitHub, and what holds an automatic
/// install back.
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

    [Fact]
    public void NeitherSettingRepublishesTheMqttSurface() =>
        // Nothing about the update routine reaches Home Assistant, so editing either must cost no
        // republish. Both directions of the list are pinned by SettingsChangeClassifierTests.
        Assert.All(new[] { nameof(AppSettings.UpdateCheckCadence), nameof(AppSettings.InstallUpdatesAutomatically) },
                   name => Assert.Contains(name, UnpublishedSettings.UnpublishedProperties));

    // ── When a tick asks ────────────────────────────────────────────────────────────────────────

    private static readonly DateTimeOffset Start = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

    // The cadence arrives by name: the enum is internal, so it cannot be a public parameter, and
    // naming it here doubles as a second reading of the stored spelling.
    [Theory]
    [InlineData("EveryHour")]
    [InlineData("EveryDay")]
    [InlineData("AtStartupOnly")]
    public void TheFirstCheckRunsUnderEveryCadence(string cadence) =>
        // Including the one that never repeats: "only at startup" is a startup run, not no run.
        Assert.True(UpdateSchedulePolicy.IsDue(Cadence(cadence), lastCheck: null, Start));

    private static UpdateCheckCadence Cadence(string name) => Enum.Parse<UpdateCheckCadence>(name);

    [Fact]
    public void OnlyAtStartup_NeverAsksAgain()
    {
        Assert.False(UpdateSchedulePolicy.IsDue(UpdateCheckCadence.AtStartupOnly, Start, Start.AddHours(1)));
        Assert.False(UpdateSchedulePolicy.IsDue(UpdateCheckCadence.AtStartupOnly, Start, Start.AddDays(30)));
        Assert.Null(UpdateSchedulePolicy.Every(UpdateCheckCadence.AtStartupOnly));
    }

    [Theory]
    // The tick is five minutes, so the hour is reached one tick short of it and then on it.
    [InlineData("EveryHour", 55, false)]
    [InlineData("EveryHour", 60, true)]
    [InlineData("EveryDay", 60, false)]
    [InlineData("EveryDay", 23 * 60 + 55, false)]
    [InlineData("EveryDay", 24 * 60, true)]
    public void ACheckIsDueOnceTheChosenGapHasPassed(string cadence, int minutes, bool due) =>
        Assert.Equal(due, UpdateSchedulePolicy.IsDue(Cadence(cadence), Start, Start.AddMinutes(minutes)));

    [Fact]
    public void TheTickIsShorterThanTheShortestCadence() =>
        // The same tick re-tests the automatic-install gate, so a tick as long as the cadence would
        // make a machine that has just gone quiet wait an hour.
        Assert.True(UpdateSchedulePolicy.TickInterval < UpdateSchedulePolicy.Every(UpdateCheckCadence.EveryHour));

    // ── What holds an automatic install back ────────────────────────────────────────────────────

    private static readonly TimeSpan LongIdle  = AutoInstallPolicy.IdleFor + TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ShortIdle = AutoInstallPolicy.IdleFor - TimeSpan.FromMinutes(1);

    // idle defaults to a long one. Not nullable: a nullable default cannot tell "left at the
    // default" from "no reading at all", which is how the failing-reading case first passed against
    // a long idle. That case calls the policy directly.
    private static AutoInstallHold Decide(
        bool enabled = true, bool hasRelease = true, TimeSpan idle = default,
        bool focus = false, bool lidWait = false) =>
        AutoInstallPolicy.Decide(enabled, hasRelease,
                                 idle == default ? LongIdle : idle, focus, lidWait);

    [Fact]
    public void AMachineLeftAlone_Installs() =>
        Assert.Equal(AutoInstallHold.None, Decide());

    [Fact]
    public void SomebodyAtTheMachine_HoldsTheInstall() =>
        Assert.Equal(AutoInstallHold.SomebodyIsAtTheMachine, Decide(idle: ShortIdle));

    [Fact]
    public void AnIdleReadingThatFailed_HoldsTheInstall() =>
        // A reading that could not be taken is no evidence that the machine is free.
        Assert.Equal(AutoInstallHold.SomebodyIsAtTheMachine,
                     AutoInstallPolicy.Decide(enabled: true, hasRelease: true, sinceLastInput: null,
                                              focusRunning: false, lidWaitRunning: false));

    [Fact]
    public void AFocusSession_HoldsTheInstall() =>
        Assert.Equal(AutoInstallHold.FocusSessionRunning, Decide(focus: true));

    [Fact]
    public void ALidCloseWait_HoldsTheInstall() =>
        // The lid is shut, so the idle reading says the machine is free exactly when it is on its
        // way to sleep — which is why this hold is tested with a long idle.
        Assert.Equal(AutoInstallHold.LidCloseWaitRunning, Decide(lidWait: true));

    [Fact]
    public void TheSwitchOffAndNoReleaseAreBothHolds()
    {
        Assert.Equal(AutoInstallHold.SwitchedOff, Decide(enabled: false));
        Assert.Equal(AutoInstallHold.NothingToInstall, Decide(hasRelease: false));
    }

    [Theory]
    [InlineData("Off", false)]
    [InlineData("Idle", false)]
    [InlineData("WaitingForTheTimer", true)]
    [InlineData("WaitingForTheBatteryTarget", true)]
    [InlineData("WaitingForEither", true)]
    [InlineData("WaitingWithNothingLeftToReach", true)]
    public void EveryWaitingStateCountsAsAWait(string state, bool waiting) =>
        Assert.Equal(waiting, LidWaitStates.IsWaiting(Enum.Parse<LidWaitState>(state)));

    // ── The install path ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnAutomaticInstallAnswersInstallAndDrawsNothing()
    {
        // The whole point of reusing the ordinary flow: the flow asks its prompts, and these
        // answers start the install without a window.
        var prompts = new SilentUpdatePrompts();
        var release = new ZeroZero.Update.ReleaseInfo("v9.9.9", new Version(9, 9, 9), "9.9.9", "",
                                                      "", new Uri("https://example.invalid"), null, []);

        Assert.Equal(ZeroZero.Update.Win32.InstallChoice.Install,
                     prompts.AskToInstallAsync(release, new Version(1, 0, 0)).GetAwaiter().GetResult());

        var surface = prompts.BeginDownload(release);
        Assert.NotNull(surface.Progress);
        Assert.False(surface.Cancelled.CanBeCanceled);
    }

    [Fact]
    public void TheAutomaticInstallGoesThroughTheOneUpdatePath()
    {
        // No second download or install path: one service, one handover launcher, and every flow —
        // the check, the offered install and the silent one — composed over that same service.
        string source = File.ReadAllText(RepoFiles.Find(Path.Combine("Services", "AppUpdates.cs")));
        int Count(string pattern) => System.Text.RegularExpressions.Regex.Matches(source, pattern).Count;

        Assert.Contains("InstallSilentlyAsync", source, StringComparison.Ordinal);
        Assert.Equal(1, Count(@"new UpdateService\("));
        Assert.Equal(1, Count(@"UpdateHandoverLauncher _launcher"));
        Assert.Equal(3, Count(@"new UpdateFlow\(_service,"));

        // Both install paths stamp the handover record; the check must not.
        Assert.Equal(2, Count(@"_launcher\.TargetVersion"));
    }
}
