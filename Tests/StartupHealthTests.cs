using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// Holds the application to the rule the 2026-09-02 start-up failure broke: an instance that is
/// watching nothing may not look like one that is working. The tray methods live on the WinUI
/// application object and cannot be constructed here, so the checks that enforce it in those
/// methods are asserted against the shipped source.
/// </summary>
public class StartupHealthTests
{
    private static string AppSource() => File.ReadAllText(RepoFiles.Find("App.xaml.cs"));

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
}
