using ChargeKeeper.Services;
using ZeroZero.Mqtt;
using ZeroZero.Mqtt.Discovery;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The command seam: one inbound payload in, and either a refusal carrying a reason or exactly one
/// call on the services the Settings window drives. Every bound enforced here is the bound the UI
/// enforces, from the same constant — a remote write can reach nothing the UI cannot.
/// </summary>
public class MqttEntityCommandTests
{
    private static MqttCommandVerdict Send(
        string entityId, string payload,
        IChargeControlActions? charge = null, ISettingsActions? settings = null,
        LiveState? live = null, SurfaceState? surface = null)
    {
        var set = MqttTestBed.Build(
            live ?? MqttTestBed.Live(), surface ?? MqttTestBed.Surface(),
            charge: charge, settings: settings);
        return MqttTestBed.Command(set, entityId).Accept(payload);
    }

    // ── Switches ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ON", true)]
    [InlineData("OFF", false)]
    [InlineData("on", true)]
    [InlineData("off", false)]
    public void ASwitchPayload_ReachesItsSetterAsTheBooleanItMeans(string payload, bool expected)
    {
        var charge = new FakeChargeControl();
        MqttTestBed.Run(Send(MqttEntityCatalog.SmartCharge, payload, charge: charge));
        Assert.Equal([expected], charge.SmartChargeSet);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("")]
    [InlineData("maybe")]
    public void AnythingButTheDeclaredOnAndOffPayloads_IsMalformedRatherThanGuessedAt(string payload) =>
        // The old parser took "1", "yes" and "true" as well. The declared pair is what the document
        // advertises, so it is the whole of what a switch takes.
        Assert.Equal(MqttCommandOutcome.Malformed, Send(MqttEntityCatalog.SmartCharge, payload).Outcome);

    [Fact]
    public void EverySettingsSwitch_ReachesItsOwnSetter()
    {
        (string EntityId, string Expected)[] cases =
        [
            (MqttEntityCatalog.KeepAwake,           "KeepAwake=True"),
            (MqttEntityCatalog.KeepAwakeDisplayOn,  "KeepAwakeDisplayOn=True"),
            (MqttEntityCatalog.LidDelay,            "LidDelay=True"),
            (MqttEntityCatalog.LidDelayLock,        "LidDelayLock=True"),
            (MqttEntityCatalog.LidDelayOffAfterSleep, "LidDelayOffAfterSleep=True"),
            (MqttEntityCatalog.SmartStandby,        "SmartStandby=True"),
            (MqttEntityCatalog.LowBatteryWarning,   "LowBatteryWarning=True"),
            (MqttEntityCatalog.HighBatteryWarning,  "HighBatteryWarning=True"),
            (MqttEntityCatalog.DrainWarning,        "DrainWarning=True"),
            (MqttEntityCatalog.NetworkProfiles,     "NetworkProfiles=True"),
        ];

        foreach (var (entityId, expected) in cases)
        {
            var settings = new FakeSettingsActions();
            MqttTestBed.Run(Send(entityId, MqttPayload.On, settings: settings));
            Assert.Equal([expected], settings.Calls);
        }
    }

    // ── Numbers ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EverySettingsNumber_ReachesItsOwnSetterWithAWholeValue()
    {
        (string EntityId, string Payload, string Expected)[] cases =
        [
            (MqttEntityCatalog.LidDelayMinutes,  "12",   "LidDelayMinutes=12"),
            (MqttEntityCatalog.LowBatteryLevel,  "20.0", "LowBatteryLevel=20"),
            (MqttEntityCatalog.HighBatteryLevel, "90",   "HighBatteryLevel=90"),
            (MqttEntityCatalog.DrainRate,        "3",    "DrainRate=3"),
            (MqttEntityCatalog.StartupDelay,     "0",    "StartupDelay=0"),
            (MqttEntityCatalog.DowntimeGap,      "15",   "DowntimeGap=15"),
        ];

        foreach (var (entityId, payload, expected) in cases)
        {
            var settings = new FakeSettingsActions();
            MqttTestBed.Run(Send(entityId, payload, settings: settings));
            Assert.Equal([expected], settings.Calls);
        }
    }

    [Theory]
    [InlineData(MqttEntityCatalog.LowBatteryLevel, "4")]
    [InlineData(MqttEntityCatalog.LowBatteryLevel, "51")]
    [InlineData(MqttEntityCatalog.HighBatteryLevel, "59")]
    [InlineData(MqttEntityCatalog.HighBatteryLevel, "96")]
    [InlineData(MqttEntityCatalog.DrainRate, "0")]
    [InlineData(MqttEntityCatalog.DrainRate, "11")]
    [InlineData(MqttEntityCatalog.StartupDelay, "-1")]
    [InlineData(MqttEntityCatalog.StartupDelay, "61")]
    [InlineData(MqttEntityCatalog.DowntimeGap, "61")]
    public void ANumberOutsideItsDeclaredBounds_IsRefusedRatherThanClamped(string entityId, string payload)
    {
        var settings = new FakeSettingsActions();
        var verdict = Send(entityId, payload, settings: settings);

        Assert.Equal(MqttCommandOutcome.OutOfRange, verdict.Outcome);
        Assert.Empty(settings.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("eighty")]
    [InlineData("80 %")]
    public void ANumberPayloadThatIsNotOne_IsMalformed(string payload) =>
        Assert.Equal(MqttCommandOutcome.Malformed,
                     Send(MqttEntityCatalog.LowBatteryLevel, payload).Outcome);

    [Fact]
    public void ADecimalCommaNeverParses_SoALocalisedPayloadCannotBeReadAsAWholeNumber() =>
        Assert.Equal(MqttCommandOutcome.Malformed,
                     Send(MqttEntityCatalog.LowBatteryLevel, "20,5").Outcome);

    // ── The two charge thresholds, which are one control split over two entities ────────────────

    [Fact]
    public void ANewStart_KeepsTheStopWhereItIs()
    {
        var charge = new FakeChargeControl { Current = (60, 80) };
        MqttTestBed.Run(Send(MqttEntityCatalog.ChargeStart, "65", charge: charge));
        Assert.Equal([(65, 80)], charge.Applied);
    }

    [Fact]
    public void AStartTooCloseToTheStop_IsHeldTheMinimumGapBelowIt()
    {
        var charge = new FakeChargeControl { Current = (60, 80) };
        MqttTestBed.Run(Send(MqttEntityCatalog.ChargeStart, "79", charge: charge));
        Assert.Equal([(80 - PresetEditValidator.MinGap, 80)], charge.Applied);
    }

    [Fact]
    public void ANewStop_KeepsTheStartWhereItIs()
    {
        var charge = new FakeChargeControl { Current = (60, 80) };
        MqttTestBed.Run(Send(MqttEntityCatalog.ChargeStop, "90", charge: charge));
        Assert.Equal([(60, 90)], charge.Applied);
    }

    [Fact]
    public void AStopTooCloseToTheStart_IsHeldTheMinimumGapAboveIt()
    {
        var charge = new FakeChargeControl { Current = (60, 80) };
        MqttTestBed.Run(Send(MqttEntityCatalog.ChargeStop, "61", charge: charge));
        Assert.Equal([(60, 60 + PresetEditValidator.MinGap)], charge.Applied);
    }

    [Theory]
    [InlineData("4")]
    [InlineData("101")]
    public void AThresholdOutsideTheDeclaredRange_IsRefusedBeforeAnyClampIsConsidered(string payload)
    {
        var charge = new FakeChargeControl();
        Assert.Equal(MqttCommandOutcome.OutOfRange,
                     Send(MqttEntityCatalog.ChargeStart, payload, charge: charge).Outcome);
        Assert.Empty(charge.Applied);
    }

    [Fact]
    public void ClampingAStart_NeverPushesItBelowTheDeclaredFloor() =>
        Assert.Equal((PresetEditValidator.MinThreshold, PresetEditValidator.MinThreshold),
                     ChargeThresholdCommands.WithStart(50, PresetEditValidator.MinThreshold));

    [Fact]
    public void ClampingAStop_NeverPushesItAboveTheDeclaredCeiling() =>
        Assert.Equal((PresetEditValidator.MaxThreshold, PresetEditValidator.MaxThreshold),
                     ChargeThresholdCommands.WithStop(50, PresetEditValidator.MaxThreshold));

    // ── The button ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheDeclaredPressPayload_TriggersTheOneShotChargeToFull()
    {
        var charge = new FakeChargeControl();
        MqttTestBed.Run(Send(MqttEntityCatalog.ChargeToFull, "PRESS", charge: charge));
        Assert.Equal(1, charge.ChargeToFullCalls);
    }

    [Theory]
    [InlineData("press")]
    [InlineData("ON")]
    [InlineData("")]
    public void AnythingElseOnTheButton_IsMalformedAndFiresNothing(string payload)
    {
        // Exact match only: a kick to 100 % must not fire on a stray payload.
        var charge = new FakeChargeControl();
        Assert.Equal(MqttCommandOutcome.Malformed,
                     Send(MqttEntityCatalog.ChargeToFull, payload, charge: charge).Outcome);
        Assert.Equal(0, charge.ChargeToFullCalls);
    }

    // ── The three selects ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void AConfiguredPresetName_IsApplied()
    {
        var charge = new FakeChargeControl();
        var settings = new FakeSettingsActions { Presets = ["Daily", "Travel"] };
        MqttTestBed.Run(Send(MqttEntityCatalog.Preset, "Travel", charge: charge, settings: settings));
        Assert.Equal(["Travel"], charge.PresetsApplied);
    }

    [Fact]
    public void APresetNameNobodyConfigured_IsRefusedAgainstTheListInForceRatherThanApplied()
    {
        var charge = new FakeChargeControl();
        var settings = new FakeSettingsActions { Presets = ["Daily"] };
        var verdict = Send(MqttEntityCatalog.Preset, "Travel", charge: charge, settings: settings);

        Assert.Equal(MqttCommandOutcome.NotAnOption, verdict.Outcome);
        Assert.Empty(charge.PresetsApplied);
    }

    [Fact]
    public void TheResetLiteral_IsAReadingRatherThanARequestAndIsRefusedOnEverySelect()
    {
        foreach (string entityId in
                 (string[])[MqttEntityCatalog.Preset, MqttEntityCatalog.UnknownNetworkPreset,
                            MqttEntityCatalog.IconMode])
            Assert.Equal(MqttCommandOutcome.NotAnOption, Send(entityId, MqttPayload.None).Outcome);
    }

    [Fact]
    public void TheStayPutSentinel_IsStoredAsNoPresetAtAll()
    {
        var settings = new FakeSettingsActions();
        MqttTestBed.Run(Send(MqttEntityCatalog.UnknownNetworkPreset,
                             PresetEditValidator.UnknownNetworkSentinel, settings: settings));
        Assert.Equal(["UnknownNetworkPreset=<null>"], settings.Calls);
    }

    [Fact]
    public void AConfiguredPreset_IsStoredAsTheUnknownNetworkChoice()
    {
        var settings = new FakeSettingsActions { Presets = ["Daily", "Travel"] };
        MqttTestBed.Run(Send(MqttEntityCatalog.UnknownNetworkPreset, "Travel", settings: settings));
        Assert.Equal(["UnknownNetworkPreset=Travel"], settings.Calls);
    }

    [Fact]
    public void EveryTrayIconStyleTheSelectOffers_ParsesBackToItsOwnMode()
    {
        foreach (string option in MqttEntityCatalog.IconModeOptions)
        {
            var settings = new FakeSettingsActions();
            MqttTestBed.Run(Send(MqttEntityCatalog.IconMode, option, settings: settings));
            Assert.Equal([$"IconMode={option}"], settings.Calls);
        }
    }

    [Fact]
    public void ATrayIconStyleThatIsNotOffered_IsRefused() =>
        Assert.Equal(MqttCommandOutcome.NotAnOption,
                     Send(MqttEntityCatalog.IconMode, "Sparkline").Outcome);

    // ── The one text entity ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("90")]
    [InlineData("1h30")]
    [InlineData("17:00")]
    public void AKeepAwakeDurationTheSettingsBoxAccepts_StartsASession(string payload)
    {
        var settings = new FakeSettingsActions();
        MqttTestBed.Run(Send(MqttEntityCatalog.KeepAwakeFor, payload, settings: settings));
        Assert.Single(settings.Calls);
        Assert.StartsWith("StartKeepAwake=", settings.Calls[0]);
    }

    [Theory]
    [InlineData("forever")]
    [InlineData("25:00")]
    [InlineData("")]
    public void AKeepAwakeDurationTheSettingsBoxRefuses_IsRefusedHereToo(string payload)
    {
        var settings = new FakeSettingsActions();
        Assert.Equal(MqttCommandOutcome.Malformed,
                     Send(MqttEntityCatalog.KeepAwakeFor, payload, settings: settings).Outcome);
        Assert.Empty(settings.Calls);
    }

    [Fact]
    public void ATextPayloadPastItsDeclaredLength_IsOutOfRangeBeforeItIsParsed()
    {
        var settings = new FakeSettingsActions();
        Assert.Equal(MqttCommandOutcome.OutOfRange,
                     Send(MqttEntityCatalog.KeepAwakeFor, new string('9', 17), settings: settings).Outcome);
        Assert.Empty(settings.Calls);
    }
}
