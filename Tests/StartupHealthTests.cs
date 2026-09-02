using ChargeKeeper.Helpers;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// Holds the application to the rule the 2026-09-02 start-up failure broke: an instance that is
/// watching nothing may not look like one that is working. The tray methods live on the WinUI
/// application object and cannot be constructed here, so the checks that enforce it in those
/// methods are asserted against the shipped source.
/// </summary>
public class StartupHealthTests : IDisposable
{
    public StartupHealthTests() => StartupHealth.ResetForTests();

    public void Dispose()
    {
        StartupHealth.ResetForTests();
        GC.SuppressFinalize(this);
    }

    private static string AppSource() => File.ReadAllText(RepoFiles.Find("App.xaml.cs"));

    [Fact]
    public void FreshProcess_IsNotYetDegradedAndNotYetWatching()
    {
        Assert.Equal(MonitoringState.Starting, StartupHealth.State);
        Assert.False(StartupHealth.IsDegraded);
    }

    [Fact]
    public void AFailedStartup_IsDegraded()
    {
        StartupHealth.MarkFailed();

        Assert.Equal(MonitoringState.Failed, StartupHealth.State);
        Assert.True(StartupHealth.IsDegraded);
    }

    [Fact]
    public void AWatchingInstance_IsNotDegraded()
    {
        StartupHealth.MarkWatching();

        Assert.False(StartupHealth.IsDegraded);
    }

    /// <summary>A deliberate exit is not a fault, so it must not put the warning mark on the icon
    /// on the way out.</summary>
    [Fact]
    public void AStoppedInstance_IsNotDegraded()
    {
        StartupHealth.MarkWatching();
        StartupHealth.MarkStopped();

        Assert.Equal(MonitoringState.Stopped, StartupHealth.State);
        Assert.False(StartupHealth.IsDegraded);
    }

    // The three tray methods that could otherwise present a failed instance as a working one. Each
    // must consult the degraded state before it does anything else; a check further down is a check
    // a later edit can walk past.
    [Theory]
    [InlineData("UpdateTrayIcon")]
    [InlineData("UpdateTooltip")]
    [InlineData("ForceIconRefresh")]
    public void EveryTrayPresentationPath_ChecksTheDegradedStateFirst(string method)
    {
        string body = SourceMethods.Body(AppSource(), method);

        int check = body.IndexOf("StartupHealth.IsDegraded", StringComparison.Ordinal);
        Assert.True(check >= 0,
            $"{method} does not consult StartupHealth.IsDegraded, so a start-up that failed would " +
            "still be presented as a working application.");

        // Nothing that paints or measures a reading may precede it.
        foreach (var painter in new[] { "RenderBatteryIcon", "_iconLatch", "AppInfo.Version" })
        {
            int at = body.IndexOf(painter, StringComparison.Ordinal);
            Assert.True(at < 0 || at > check,
                $"{method} reaches '{painter}' before checking StartupHealth.IsDegraded.");
        }
    }

    /// <summary>The tray-icon creation must not be able to abandon start-up. This is the exact
    /// 2026-09-02 failure: ForceCreate threw, the launch handler unwound, and nothing was watched.</summary>
    [Fact]
    public void PlacingTheTrayIcon_CannotAbandonStartup()
    {
        string body = SourceMethods.Body(AppSource(), "InitTrayIcon");

        int create = body.IndexOf("ForceCreate", StringComparison.Ordinal);
        Assert.True(create >= 0, "InitTrayIcon no longer creates the tray icon.");

        string tail = body[create..];
        Assert.Contains("catch", tail, StringComparison.Ordinal);
        Assert.Contains("InitTrayIcon.ForceCreate", tail, StringComparison.Ordinal);
    }

    /// <summary>The rest of start-up runs under one guard, so no throw inside it can leave a tray
    /// icon standing over an application that subscribed to nothing.</summary>
    [Fact]
    public void TheStartupGate_ReportsAFailureRatherThanUnwindingSilently()
    {
        string body = SourceMethods.Body(AppSource(), "OnLaunched");

        int start = body.IndexOf("StartMonitoring()", StringComparison.Ordinal);
        Assert.True(start >= 0, "OnLaunched no longer starts monitoring through StartMonitoring().");
        Assert.Contains("ReportStartupFailed()", body[start..], StringComparison.Ordinal);
    }

    /// <summary>The battery subscription IS the watch, and it is established on a background task
    /// long after the launch handler returned — its own failure has to reach the same report.</summary>
    [Fact]
    public void AFailedBatterySubscription_ReportsTheSameFailure()
    {
        string body = SourceMethods.Body(AppSource(), "SubscribeBatteryEvents");

        Assert.Contains("ReportMonitoringStarted()", body, StringComparison.Ordinal);
        Assert.Contains("ReportStartupFailed()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MonitoringStarted_NamesTheReadingAndBothWarningLevels()
    {
        string line = HealthMessages.MonitoringStarted(47, PowerState.Discharging,
            lowEnabled: true, lowPercent: 40, highEnabled: true, highPercent: 80);

        Assert.Equal(
            "Battery monitoring started. The battery is at 47 % and discharging. " +
            "A warning is set for 40 % on the way down and 80 % on the way up.", line);
    }

    [Fact]
    public void MonitoringStarted_WithOnlyTheLowWarningOn_NamesOnlyThatLevel()
    {
        string line = HealthMessages.MonitoringStarted(90, PowerState.Charging,
            lowEnabled: true, lowPercent: 40, highEnabled: false, highPercent: 80);

        Assert.Equal(
            "Battery monitoring started. The battery is at 90 % and charging. " +
            "A warning is set for 40 % on the way down.", line);
    }

    [Fact]
    public void MonitoringStarted_WithNoWarningsOn_SaysSoRatherThanNamingALevel()
    {
        string line = HealthMessages.MonitoringStarted(62, PowerState.IdleOnMains,
            lowEnabled: false, lowPercent: 40, highEnabled: false, highPercent: 80);

        Assert.EndsWith("No battery warnings are set.", line, StringComparison.Ordinal);
        Assert.Contains("on mains without charging", line, StringComparison.Ordinal);
    }

    /// <summary>The line is the whole point of the change: it has to state the consequence, not
    /// the cause, because the cause is already at Error level and is not what a person needs.</summary>
    [Fact]
    public void MonitoringDidNotStart_SaysNoWarningsWillBeGiven()
    {
        Assert.Contains("no battery warnings will be given",
                        HealthMessages.MonitoringDidNotStart, StringComparison.Ordinal);
    }

    [Fact]
    public void MonitoringStopped_SaysWarningsStopWithIt()
    {
        Assert.Equal(
            "Battery monitoring stopped. No battery warnings will be given until the application " +
            "runs again.", HealthMessages.MonitoringStopped);
    }

    /// <summary>NOTIFYICONDATA.szTip holds 127 UTF-16 characters and the shell truncates the rest
    /// without a word, so a tooltip that says something is wrong must fit whole.</summary>
    [Fact]
    public void TheDegradedTooltip_FitsTheShellsTooltipLimit()
    {
        Assert.True(HealthMessages.DegradedTooltip.Length <= 127,
            $"the degraded tooltip is {HealthMessages.DegradedTooltip.Length} characters and would " +
            "be truncated by the shell.");
    }

    [Fact]
    public void TheDegradedTooltip_SaysTheBatteryIsNotBeingWatched()
    {
        Assert.Contains("not watching the battery",
                        HealthMessages.DegradedTooltip, StringComparison.Ordinal);
        Assert.Contains("No battery warnings will be given",
                        HealthMessages.DegradedTooltip, StringComparison.Ordinal);
    }

    /// <summary>The warning mark must be a mark of its own, not a battery reading in disguise: a
    /// reading the user could mistake for normal is the failure this whole change is about.</summary>
    [Fact]
    public void TheWarningMark_LooksLikeNoBatteryReadingTheAppCanShow()
    {
        const int size = 32;
        using var warning = IconGenerator.RenderWarningBitmap(size);

        Assert.Equal(size, warning.Width);
        Assert.Equal(size, warning.Height);

        foreach (var mode in Enum.GetValues<TrayIconMode>())
            for (int percent = 0; percent <= 100; percent += 5)
                foreach (var state in Enum.GetValues<PowerState>())
                {
                    using var reading = IconGenerator.RenderStyleBitmap(size, percent, state, mode);
                    Assert.False(PixelsMatch(warning, reading),
                        $"the warning mark is indistinguishable from the {mode} style at " +
                        $"{percent} % {state}.");
                }
    }

    private static bool PixelsMatch(System.Drawing.Bitmap a, System.Drawing.Bitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return false;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y)) return false;
        return true;
    }
}
