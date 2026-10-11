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

    /// <summary>A start whose tray icon never appears is unreachable and holds the single-instance
    /// lock, so it ends before any window is built, through the crash handlers' report. That exit
    /// must not read as deliberate: the hold marker would keep the watchdog task from retrying.</summary>
    [Fact]
    public void AFailedTrayStart_EndsBeforeAnyWindowWithoutMarkingTheExitDeliberate()
    {
        string source = AppSource();
        int cleanup = source.IndexOf("OnStartupFailure =", StringComparison.Ordinal);
        Assert.True(cleanup >= 0, "Without OnStartupFailure a report does not end a failed start.");

        string body    = SourceMethods.Body(source, "OnLaunched");
        int    tray    = body.IndexOf("InitTrayIcon()", StringComparison.Ordinal);
        int    report  = body.IndexOf("_crashHandlers.Report(\"tray\"", StringComparison.Ordinal);
        int    windows = body.IndexOf("StartMonitoring()", StringComparison.Ordinal);
        int    done    = body.IndexOf("MarkStartupComplete()", StringComparison.Ordinal);
        Assert.True(tray >= 0 && tray < report && report < windows && windows < done,
            "OnLaunched must place the icon, report a failure, build windows, then mark start-up complete.");

        string failedStart = body[tray..body.IndexOf("return;", report, StringComparison.Ordinal)] +
                             source[cleanup..source.IndexOf("});", cleanup, StringComparison.Ordinal)];
        foreach (var deliberate in new[] { "WriteHoldMarker", "_intentionalExit = true", "Shutdown(" })
            Assert.DoesNotContain(deliberate, failedStart, StringComparison.Ordinal);
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
