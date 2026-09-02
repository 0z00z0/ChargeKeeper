using System.Text.RegularExpressions;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The notification path recorded nothing at all: not the decision, not the attempt, not the
/// refusal. These pin the sentences it now carries, and the absence of the empty catch blocks that
/// hid a user-visible failure.
/// </summary>
public class NotificationLoggingTests
{
    private static string ToastSource() => File.ReadAllText(RepoFiles.Find("Services/ToastService.cs"));

    private static string AppSource() => File.ReadAllText(RepoFiles.Find("App.xaml.cs"));

    /// <summary>A catch whose body is empty, or holds nothing but comments — the shape that made a
    /// refused notification indistinguishable from one that was never raised.</summary>
    private static readonly Regex SwallowingCatch =
        new(@"catch\s*(\([^)]*\))?\s*\{\s*(//[^\r\n]*\s*)*\}", RegexOptions.Compiled);

    [Fact]
    public void ShowingANotification_NoLongerSwallowsItsFailures()
    {
        var swallowed = SwallowingCatch.Matches(ToastSource());

        Assert.True(swallowed.Count == 0,
            $"ToastService still has {swallowed.Count} catch block(s) that discard the failure. A " +
            "notification the user never sees must reach the log.");
    }

    [Fact]
    public void EveryFailurePathInToastService_WritesAReadableLineAndKeepsTheDetail()
    {
        string source = ToastSource();

        // The readable sentence at information level, the exception behind it at error level.
        Assert.Contains("AppLog.Info(NotificationMessages.Unavailable)", source, StringComparison.Ordinal);
        Assert.Contains("AppLog.Info(NotificationMessages.CouldNotBeShown", source, StringComparison.Ordinal);
        Assert.Contains("AppLog.Info(NotificationMessages.Shown", source, StringComparison.Ordinal);
        Assert.Contains("AppLog.Error(\"ToastService.Register\"", source, StringComparison.Ordinal);
        Assert.Contains("AppLog.Error(\"ToastService.Show\"", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// The decision to warn is recorded before the attempt to show it, so "decided but not shown"
    /// and "never decided" stop looking the same in the log.
    /// </summary>
    [Fact]
    public void TheLowBatteryDecision_IsRecordedBeforeTheNotificationIsAttempted()
    {
        string body = SourceMethods.Body(AppSource(), "OnBatteryReportUpdated");

        int decision = body.IndexOf("NotificationMessages.LowThresholdCrossed", StringComparison.Ordinal);
        int attempt  = body.IndexOf("ToastService.NotifyLowBattery", StringComparison.Ordinal);

        Assert.True(decision >= 0, "the low-battery threshold crossing is not recorded at all.");
        Assert.True(attempt  >= 0, "the low-battery notification is no longer raised.");
        Assert.True(decision < attempt,
            "the crossing is recorded after the notification is attempted, so a failure to show " +
            "would be logged before the decision that caused it.");
    }

    [Fact]
    public void ASuppressedRepeatAndAReArm_AreBothRecorded()
    {
        string body = SourceMethods.Body(AppSource(), "OnBatteryReportUpdated");

        Assert.Contains("NotificationMessages.LowRepeatSuppressed", body, StringComparison.Ordinal);
        Assert.Contains("NotificationMessages.LowWarningReArmed", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Unavailable_SaysNoWarningsCanBeShownAtAll()
    {
        Assert.Equal(
            "Notifications are unavailable — Windows did not accept the application's registration, " +
            "so no battery warnings can be shown.", NotificationMessages.Unavailable);
    }

    [Fact]
    public void Shown_CarriesTheLevelItWasShownAt()
    {
        Assert.Equal("A low-battery warning was shown on screen at 39 %.",
                     NotificationMessages.Shown(NotificationKind.LowBattery, 39));
    }

    [Fact]
    public void Shown_WithoutALevel_OmitsIt()
    {
        Assert.Equal("A charging-started notice was shown on screen.",
                     NotificationMessages.Shown(NotificationKind.ChargingStarted, null));
    }

    [Fact]
    public void CouldNotBeShown_CarriesTheLevelAndWhatWindowsSaid()
    {
        Assert.Equal(
            "A low-battery warning could not be shown at 39 %. Windows refused it: Class not registered.",
            NotificationMessages.CouldNotBeShown(NotificationKind.LowBattery, 39, "Class not registered."));
    }

    /// <summary>A Windows failure text runs to several lines often enough that an unflattened one
    /// would split the entry into fragments nothing attributes back to the warning.</summary>
    [Fact]
    public void CouldNotBeShown_FlattensAMultiLineWindowsReason()
    {
        string line = NotificationMessages.CouldNotBeShown(
            NotificationKind.HighBattery, 82, "The call failed.\r\nSee the inner exception.");

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.Contains("The call failed. See the inner exception.", line, StringComparison.Ordinal);
    }

    [Fact]
    public void CouldNotBeShown_WithNothingFromWindows_StillReadsAsASentence()
    {
        Assert.EndsWith("no reason given.",
                        NotificationMessages.CouldNotBeShown(NotificationKind.DrainAnomaly, null, "   "),
                        StringComparison.Ordinal);
    }

    [Fact]
    public void LowThresholdCrossed_CarriesTheLevelAndTheThreshold()
    {
        Assert.Equal("The battery fell past the 40 % warning level, reaching 39 % while discharging.",
                     NotificationMessages.LowThresholdCrossed(40, 39));
    }

    [Fact]
    public void LowRepeatSuppressed_SaysAWarningWasAlreadyGiven()
    {
        Assert.Equal(
            "The battery is below the 40 % warning level at 33 %, but a warning has already been " +
            "given for this discharge.", NotificationMessages.LowRepeatSuppressed(40, 33));
    }

    [Fact]
    public void LowWarningReArmed_SaysTheWarningCanBeGivenAgain()
    {
        Assert.Equal("The battery rose to 46 %, so the low-battery warning is ready to be given again.",
                     NotificationMessages.LowWarningReArmed(46));
    }

    [Fact]
    public void LowWarningResetByRestart_ExplainsTheRepeatARestartCauses()
    {
        Assert.Equal(
            "The low-battery warning was reset when the application restarted. A warning will be " +
            "given again below 40 %.", NotificationMessages.LowWarningResetByRestart(40));
    }
}
