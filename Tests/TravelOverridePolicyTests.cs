using System.Text.RegularExpressions;
using ChargeKeeper.Services;
using Windows.System.Power;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// "Charge to 100 % once" lifts the charge cap for one charge. The endings are what make the word
/// "once" true: without the charger-removed ending the lift outlives the charge it was asked for and
/// every later charge runs to 100 % as well.
/// </summary>
public class TravelOverridePolicyTests
{
    /// <summary>A process that has not seen a reading yet reports this as the previous one, which is
    /// what every ending has to cope with after a restart or a crash.</summary>
    private const BatteryStatus NoPreviousReading = BatteryStatus.NotPresent;

    private static TravelOverrideStep Decide(
        BatteryStatus status, bool chargeStarted = true, int pct = 70,
        BatteryStatus lastStatus = NoPreviousReading, bool active = true) =>
        TravelOverridePolicy.Decide(active, chargeStarted, pct, status, lastStatus);

    [Fact]
    public void TheLiftEndsWhenTheChargerComesOutBeforeTheBatteryIsFull()
    {
        Assert.Equal(TravelOverrideStep.Revert,
            Decide(BatteryStatus.Discharging, chargeStarted: true, pct: 70,
                   lastStatus: BatteryStatus.Charging));
    }

    [Fact]
    public void TheLiftEndsWhenTheChargerIsAlreadyGoneAtTheFirstReadingOfANewRun()
    {
        // The charge began, then the machine was shut down and the charger taken out. The new run
        // has no previous reading of its own, so the ending has to come off the persisted record
        // alone — the one case a lift kept only in memory would miss for good.
        Assert.Equal(TravelOverrideStep.Revert,
            Decide(BatteryStatus.Discharging, chargeStarted: true, lastStatus: NoPreviousReading));
    }

    [Fact]
    public void ALiftArmedWhileUnpluggedWaitsForItsCharger()
    {
        // The travel case the feature is named for: asked for before setting off, charged overnight.
        Assert.Equal(TravelOverrideStep.Hold,
            Decide(BatteryStatus.Discharging, chargeStarted: false));
    }

    [Fact]
    public void TheFirstChargingReadingIsWrittenDown()
    {
        Assert.Equal(TravelOverrideStep.RecordChargeStarted,
            Decide(BatteryStatus.Charging, chargeStarted: false));
    }

    [Fact]
    public void ChargingOnIsNotAnEnding()
    {
        Assert.Equal(TravelOverrideStep.Hold, Decide(BatteryStatus.Charging, chargeStarted: true));
    }

    [Fact]
    public void TheLiftEndsWhenChargingCompletes()
    {
        // Catches a worn pack that settles below 100 %.
        Assert.Equal(TravelOverrideStep.Revert,
            Decide(BatteryStatus.Idle, pct: 97, lastStatus: BatteryStatus.Charging));
    }

    [Fact]
    public void TheLiftEndsAtFullOnFirmwareThatNeverReportsAChargingPhase()
    {
        Assert.Equal(TravelOverrideStep.Revert,
            Decide(BatteryStatus.Idle, pct: 100, lastStatus: NoPreviousReading));
    }

    [Fact]
    public void NoLiftInForceMeansNothingToDecide()
    {
        Assert.Equal(TravelOverrideStep.Hold,
            Decide(BatteryStatus.Discharging, chargeStarted: true, active: false));
    }

    [Fact]
    public void SittingPluggedInBelowTheCapIsNotAnEnding()
    {
        // Idle under 100 % with no charging phase behind it: the charge has not finished.
        Assert.Equal(TravelOverrideStep.Hold,
            Decide(BatteryStatus.Idle, chargeStarted: true, pct: 92, lastStatus: BatteryStatus.Idle));
    }

    // ---- Where the decision is carried out -------------------------------------------------

    private static string ServiceSource() =>
        Regex.Replace(File.ReadAllText(RepoFiles.Find("Services/TravelOverrideService.cs")),
                      @"//[^\r\n]*", string.Empty);

    [Fact]
    public void WhetherTheChargeBeganIsReadFromTheSettingsDocument()
    {
        // Held in memory it would be lost by a restart, and a lift whose charge had begun would
        // then never end when the charger turned out to be gone.
        Assert.Matches(@"ChargeStarted\s*=>\s*SettingsService\.Current\.TravelOverrideChargeStarted",
                       ServiceSource());
    }

    [Fact]
    public void TheParkedThresholdsReachTheRecordBeforeTheDeviceIsTouched()
    {
        // The parked pair is the only record of the user's real thresholds. Written after the cap
        // came off, a crash in between would leave the cap off with nothing to put back.
        string body    = SourceMethods.Body(ServiceSource(), "Activate");
        int    saved   = body.IndexOf("TravelOverrideRevertStart", StringComparison.Ordinal);
        int    written = body.IndexOf("SetEnabled", StringComparison.Ordinal);

        Assert.True(saved   >= 0, "Activate no longer parks the thresholds.");
        Assert.True(written >= 0, "Activate no longer lifts the cap on the device.");
        Assert.True(saved < written,
            "Activate must park the thresholds before it lifts the cap on the device.");
    }

    [Fact]
    public void EveryEndingIsReadFromThePolicy()
    {
        // Two readings of when the lift is over would drift, and the one nobody looks at is the one
        // that keeps the cap off.
        string body = SourceMethods.Body(ServiceSource(), "OnBatteryReport");
        Assert.Contains("TravelOverridePolicy.Decide", body, StringComparison.Ordinal);
    }
}
