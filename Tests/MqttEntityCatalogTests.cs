using System;
using System.Collections.Generic;
using System.Linq;
using ChargeKeeper.Services;
using ZeroZero.Mqtt;
using ZeroZero.Mqtt.Discovery;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The published surface as a declaration: the forty-three entity ids, the component each is announced
/// under, and the discovery keys that decide how a receiver draws it.
/// </summary>
/// <remarks>
/// The expectations here are the values ChargeKeeper published before the device document existed,
/// written out rather than derived. An entity id composes half of a <c>unique_id</c>, so changing one
/// silently discards the name, the entity id, the area, the labels and every automation a user
/// attached to that entity — and there is no recovery. This table is what makes that a failing test
/// rather than an upgrade nobody notices.
/// </remarks>
public class MqttEntityCatalogTests
{
    private sealed record Expected(
        string EntityId,
        string Platform,
        string Name,
        string Group,
        MqttEntityCategory Category,
        string? Icon = null,
        string? DeviceClass = null,
        string? Unit = null);

    private static readonly Expected[] _table =
    [
        new(MqttEntityCatalog.BatteryLevel, "sensor", "Battery level",
            MqttPublishGroups.BatteryStatus, MqttEntityCategory.Primary, DeviceClass: "battery", Unit: "%"),
        new(MqttEntityCatalog.BatteryState, "sensor", "Battery state",
            MqttPublishGroups.BatteryStatus, MqttEntityCategory.Primary, Icon: "mdi:battery-charging"),
        new(MqttEntityCatalog.BatteryPower, "sensor", "Battery power",
            MqttPublishGroups.BatteryStatus, MqttEntityCategory.Primary, DeviceClass: "power", Unit: "W"),
        new(MqttEntityCatalog.IsCharging, "binary_sensor", "Is charging",
            MqttPublishGroups.BatteryStatus, MqttEntityCategory.Primary, DeviceClass: "battery_charging"),
        new(MqttEntityCatalog.OnAc, "binary_sensor", "On AC",
            MqttPublishGroups.BatteryStatus, MqttEntityCategory.Primary, DeviceClass: "plug"),
        new(MqttEntityCatalog.PowerState, "sensor", "Battery power state",
            MqttPublishGroups.BatteryStatus, MqttEntityCategory.Diagnostic, Icon: "mdi:power-plug-battery"),
        new(MqttEntityCatalog.BatteryHealth, "sensor", "Battery health",
            MqttPublishGroups.BatteryStatus, MqttEntityCategory.Diagnostic, Icon: "mdi:heart-pulse"),
        new(MqttEntityCatalog.RemainingChargeTime, "sensor", "Battery remaining charge time",
            MqttPublishGroups.BatteryStatus, MqttEntityCategory.Diagnostic,
            Icon: "mdi:timer-sand", DeviceClass: "duration", Unit: "min"),
        new(MqttEntityCatalog.AdapterWatts, "sensor", "Adapter rating",
            MqttPublishGroups.BatteryStatus, MqttEntityCategory.Diagnostic, DeviceClass: "power", Unit: "W"),
        new(MqttEntityCatalog.CapacityFull, "sensor", "Battery full-charge capacity",
            MqttPublishGroups.BatteryStatus, MqttEntityCategory.Diagnostic,
            DeviceClass: "energy_storage", Unit: "Wh"),
        new(MqttEntityCatalog.CapacityDesign, "sensor", "Battery design capacity",
            MqttPublishGroups.BatteryStatus, MqttEntityCategory.Diagnostic,
            DeviceClass: "energy_storage", Unit: "Wh"),
        new(MqttEntityCatalog.LowPowerMode, "binary_sensor", "Low power mode",
            MqttPublishGroups.BatteryStatus, MqttEntityCategory.Diagnostic, Icon: "mdi:leaf"),

        new(MqttEntityCatalog.SmartCharge, "switch", "Smart Charge",
            MqttPublishGroups.SmartCharge, MqttEntityCategory.Primary, Icon: "mdi:battery-heart-variant"),
        new(MqttEntityCatalog.ChargeStart, "number", "Charge threshold start",
            MqttPublishGroups.SmartCharge, MqttEntityCategory.Config, Icon: "mdi:battery-arrow-up", Unit: "%"),
        new(MqttEntityCatalog.ChargeStop, "number", "Charge threshold end",
            MqttPublishGroups.SmartCharge, MqttEntityCategory.Config, Icon: "mdi:battery-arrow-down", Unit: "%"),
        new(MqttEntityCatalog.ChargeToFull, "button", "Charge to 100 % once",
            MqttPublishGroups.SmartCharge, MqttEntityCategory.Primary, Icon: "mdi:battery-charging-100"),
        new(MqttEntityCatalog.Preset, "select", "Charge preset",
            MqttPublishGroups.SmartCharge, MqttEntityCategory.Primary, Icon: "mdi:playlist-check"),
        new(MqttEntityCatalog.TravelOverride, "binary_sensor", "Charge to full in progress",
            MqttPublishGroups.SmartCharge, MqttEntityCategory.Diagnostic, Icon: "mdi:airplane"),

        new(MqttEntityCatalog.KeepAwake, "switch", "Keep awake",
            MqttPublishGroups.KeepAwake, MqttEntityCategory.Primary, Icon: "mdi:coffee"),
        new(MqttEntityCatalog.KeepAwakeFor, "text", "Keep awake for",
            MqttPublishGroups.KeepAwake, MqttEntityCategory.Config, Icon: "mdi:timer-cog-outline"),
        new(MqttEntityCatalog.KeepAwakeExpires, "sensor", "Keep awake until",
            MqttPublishGroups.KeepAwake, MqttEntityCategory.Diagnostic, DeviceClass: "timestamp"),
        new(MqttEntityCatalog.KeepAwakeDisplayOn, "switch", "Keep the display on",
            MqttPublishGroups.KeepAwake, MqttEntityCategory.Config, Icon: "mdi:monitor-shimmer"),

        new(MqttEntityCatalog.LidDelay, "switch", "Lid-close active",
            MqttPublishGroups.LidClose, MqttEntityCategory.Config, Icon: "mdi:laptop"),
        new(MqttEntityCatalog.LidDelayTime, "switch", "Lid-close delay",
            MqttPublishGroups.LidClose, MqttEntityCategory.Config, Icon: "mdi:timer-outline"),
        new(MqttEntityCatalog.LidDelayMinutes, "number", "Lid-close delay length",
            MqttPublishGroups.LidClose, MqttEntityCategory.Config, Icon: "mdi:timer-outline", Unit: "min"),
        new(MqttEntityCatalog.LidDischarge, "switch", "Lid-close battery target",
            MqttPublishGroups.LidClose, MqttEntityCategory.Config, Icon: "mdi:battery-arrow-down"),
        new(MqttEntityCatalog.LidDischargePercent, "number", "Lid-close battery target level",
            MqttPublishGroups.LidClose, MqttEntityCategory.Config, Icon: "mdi:battery-arrow-down", Unit: "%"),
        new(MqttEntityCatalog.LidDelayLock, "switch", "Lid-close lock",
            MqttPublishGroups.LidClose, MqttEntityCategory.Config, Icon: "mdi:lock"),
        new(MqttEntityCatalog.LidDelayOffAfterSleep, "switch", "Lid-close off after sleeping",
            MqttPublishGroups.LidClose, MqttEntityCategory.Config, Icon: "mdi:numeric-1-box-outline"),
        new(MqttEntityCatalog.SmartStandby, "switch", "Smart Standby",
            MqttPublishGroups.LidClose, MqttEntityCategory.Primary, Icon: "mdi:sleep"),

        new(MqttEntityCatalog.LowBatteryWarning, "switch", "Notify low battery",
            MqttPublishGroups.Notifications, MqttEntityCategory.Config, Icon: "mdi:battery-alert"),
        new(MqttEntityCatalog.LowBatteryLevel, "number", "Notify low battery level",
            MqttPublishGroups.Notifications, MqttEntityCategory.Config, Icon: "mdi:battery-low", Unit: "%"),
        new(MqttEntityCatalog.HighBatteryWarning, "switch", "Notify high battery",
            MqttPublishGroups.Notifications, MqttEntityCategory.Config, Icon: "mdi:battery-alert-variant"),
        new(MqttEntityCatalog.HighBatteryLevel, "number", "Notify high battery level",
            MqttPublishGroups.Notifications, MqttEntityCategory.Config, Icon: "mdi:battery-high", Unit: "%"),
        new(MqttEntityCatalog.DrainWarning, "switch", "Notify standby drain",
            MqttPublishGroups.Notifications, MqttEntityCategory.Config, Icon: "mdi:battery-clock"),
        new(MqttEntityCatalog.DrainRate, "number", "Notify standby drain rate",
            MqttPublishGroups.Notifications, MqttEntityCategory.Config, Icon: "mdi:speedometer-slow", Unit: "%/h"),

        new(MqttEntityCatalog.NetworkProfiles, "switch", "Network profiles",
            MqttPublishGroups.Network, MqttEntityCategory.Config, Icon: "mdi:map-marker-radius"),
        new(MqttEntityCatalog.UnknownNetworkPreset, "select", "Network fallback preset",
            MqttPublishGroups.Network, MqttEntityCategory.Config, Icon: "mdi:map-marker-question"),
        new(MqttEntityCatalog.NetworkAdapterAlias, "sensor", "Network adapter alias",
            MqttPublishGroups.Network, MqttEntityCategory.Diagnostic, Icon: "mdi:lan-connect"),
        new(MqttEntityCatalog.NetworkIpAddress, "sensor", "Network IP address",
            MqttPublishGroups.Network, MqttEntityCategory.Diagnostic, Icon: "mdi:ip-network"),
        new(MqttEntityCatalog.NetworkAdapterName, "sensor", "Network adapter",
            MqttPublishGroups.Network, MqttEntityCategory.Diagnostic, Icon: "mdi:expansion-card"),
        new(MqttEntityCatalog.NetworkProfileMatched, "sensor", "Network profile",
            MqttPublishGroups.Network, MqttEntityCategory.Diagnostic, Icon: "mdi:map-marker-check"),

        new(MqttEntityCatalog.AppVersion, "sensor", "App version",
            MqttPublishGroups.AppDiagnostics, MqttEntityCategory.Diagnostic, Icon: "mdi:tag-outline"),
        new(MqttEntityCatalog.StartupDelay, "number", "App startup delay",
            MqttPublishGroups.AppDiagnostics, MqttEntityCategory.Config, Icon: "mdi:clock-start", Unit: "s"),
        new(MqttEntityCatalog.IconMode, "select", "App tray icon style",
            MqttPublishGroups.AppDiagnostics, MqttEntityCategory.Config, Icon: "mdi:image-outline"),
        new(MqttEntityCatalog.DowntimeGap, "number", "App downtime gap threshold",
            MqttPublishGroups.AppDiagnostics, MqttEntityCategory.Config, Icon: "mdi:chart-timeline-variant", Unit: "min"),
    ];

    public static TheoryData<string> EveryEntityId()
    {
        var data = new TheoryData<string>();
        foreach (var row in _table) data.Add(row.EntityId);
        return data;
    }

    private static Expected Row(string entityId) => _table.Single(r => r.EntityId == entityId);

    private static string? UnitOf(MqttEntity entity) => entity switch
    {
        MqttSensor sensor => sensor.Unit,
        MqttNumber number => number.Unit,
        _ => null,
    };

    [Fact]
    public void TheTable_HoldsExactlyTheFortySixEntitiesTheAppPublishes() =>
        Assert.Equal(
            _table.Select(r => r.EntityId).Order(StringComparer.Ordinal),
            MqttTestBed.Declared().All.Select(e => e.EntityId).Order(StringComparer.Ordinal));

    [Fact]
    public void TheEntityMix_IsFifteenSensorsThirteenSwitchesNineNumbersFourBinaryThreeSelectsAButtonAndAText()
    {
        var byPlatform = MqttTestBed.Declared().All
            .GroupBy(e => e.Platform)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        Assert.Equal(
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["sensor"] = 15, ["switch"] = 13, ["number"] = 9,
                ["binary_sensor"] = 4, ["select"] = 3, ["button"] = 1, ["text"] = 1,
            },
            byPlatform);
    }

    [Theory]
    [MemberData(nameof(EveryEntityId))]
    public void EveryEntity_KeepsTheComponentNameAndCategoryItWasPublishedWith(string entityId)
    {
        var expected = Row(entityId);
        var entity = MqttTestBed.Declared().Find(entityId);

        Assert.NotNull(entity);
        Assert.Equal(expected.Platform, entity.Platform);
        Assert.Equal(expected.Name, entity.Name);
        Assert.Equal(expected.Group, entity.Group);
        Assert.Equal(expected.Category, entity.Category);
    }

    [Theory]
    [MemberData(nameof(EveryEntityId))]
    public void EveryEntity_KeepsTheIconDeviceClassAndUnitItWasPublishedWith(string entityId)
    {
        var expected = Row(entityId);
        var entity = MqttTestBed.Declared().Find(entityId)!;

        Assert.Equal(expected.Icon, entity.Icon);
        Assert.Equal(expected.DeviceClass, entity.DeviceClass);
        Assert.Equal(expected.Unit, UnitOf(entity));
    }

    [Theory]
    [MemberData(nameof(EveryEntityId))]
    public void EveryEntityId_IsTopicSafeAndComposesItsOwnTopics(string entityId)
    {
        const string deviceId = "chargekeeper_office_x1";

        Assert.Equal(entityId, MqttEntityId.Normalise(entityId));
        Assert.Null(MqttTopics.ValidateChannelKey(entityId));
        Assert.Equal($"chargekeeper/{deviceId}/{entityId}",
                     MqttTopics.Channel(MqttPublisher.TopicRoot, deviceId, entityId));
        Assert.Equal($"chargekeeper/{deviceId}/cmd/{entityId}",
                     MqttTopics.Command(MqttPublisher.TopicRoot, deviceId, entityId));
    }

    [Fact]
    public void LowPowerMode_IsABinarySensorOnTheSameTopicConventionAsTheRest()
    {
        const string deviceId = "chargekeeper_office_x1";
        var entity = MqttTestBed.Declared().Find(MqttEntityCatalog.LowPowerMode);

        Assert.NotNull(entity);
        Assert.Equal("binary_sensor", entity.Platform);
        Assert.Equal("low_power_mode", entity.EntityId);
        Assert.Equal($"chargekeeper/{deviceId}/low_power_mode",
                     MqttTopics.Channel(MqttPublisher.TopicRoot, deviceId, entity.EntityId));
    }

    [Fact]
    public void LowPowerMode_ReportsTheEnergySaverFlagTheLiveSnapshotCarries()
    {
        // Read-only: Windows owns Energy Saver, so the entity reflects the flag and takes no command.
        static string? Read(bool on) =>
            MqttTestBed.Build(MqttTestBed.Live(lowPowerMode: on), MqttTestBed.Surface())
                       .Find(MqttEntityCatalog.LowPowerMode)!.ReadState();

        Assert.Equal(MqttPayload.On, Read(true));
        Assert.Equal(MqttPayload.Off, Read(false));
        Assert.False(MqttTestBed.Declared().Find(MqttEntityCatalog.LowPowerMode)!.IsCommand);
    }

    [Fact]
    public void TheDefaultDeviceId_IsTheNodeIdTheAppHasAlwaysPublishedUnder() =>
        // The unique_id is <deviceId>_<entityId>, so an installation only keeps its entities if the
        // module derives the same id from the machine name that HaDiscovery.NodeId used to.
        Assert.Equal("chargekeeper_office_x1",
                     MqttIdentity.Default(MqttPublisher.TopicRoot, "Office-X1"));

    [Fact]
    public void ADeviceIdOfOnlyPunctuation_FallsBackRatherThanComposingATopicOfUnderscores() =>
        Assert.Equal("chargekeeper_device", MqttIdentity.Default(MqttPublisher.TopicRoot, "!!!"));

    [Fact]
    public void EveryNumber_DeclaresTheBoundsTheSettingsWindowEnforces()
    {
        var set = MqttTestBed.Declared();
        void Bounds(string entityId, double min, double max, MqttNumberMode mode)
        {
            var number = Assert.IsType<MqttNumber>(set.Find(entityId));
            Assert.Equal(min, number.Min);
            Assert.Equal(max, number.Max);
            Assert.Equal(1, number.Step);
            Assert.Equal(mode, number.Mode);
        }

        Bounds(MqttEntityCatalog.ChargeStart, PresetEditValidator.MinThreshold,
               PresetEditValidator.MaxThreshold, MqttNumberMode.Slider);
        Bounds(MqttEntityCatalog.ChargeStop, PresetEditValidator.MinThreshold,
               PresetEditValidator.MaxThreshold, MqttNumberMode.Slider);
        Bounds(MqttEntityCatalog.LidDelayMinutes, LidDelayPolicy.MinMinutes,
               LidDelayPolicy.MaxMinutes, MqttNumberMode.Box);
        Bounds(MqttEntityCatalog.LowBatteryLevel, SettingRanges.LowBatteryMin,
               SettingRanges.LowBatteryMax, MqttNumberMode.Box);
        Bounds(MqttEntityCatalog.HighBatteryLevel, SettingRanges.HighBatteryMin,
               SettingRanges.HighBatteryMax, MqttNumberMode.Box);
        Bounds(MqttEntityCatalog.DrainRate, SettingRanges.DrainRateMin,
               SettingRanges.DrainRateMax, MqttNumberMode.Box);
        Bounds(MqttEntityCatalog.StartupDelay, SettingRanges.StartupDelayMin,
               SettingRanges.StartupDelayMax, MqttNumberMode.Box);
        Bounds(MqttEntityCatalog.DowntimeGap, SettingRanges.DowntimeGapMin,
               SettingRanges.DowntimeGapMax, MqttNumberMode.Box);
    }

    [Fact]
    public void TheKeepAwakeTextBox_KeepsItsSixteenCharacterCeiling() =>
        Assert.Equal(16, Assert.IsType<MqttText>(
            MqttTestBed.Declared().Find(MqttEntityCatalog.KeepAwakeFor)).MaxLength);

    [Fact]
    public void TheButton_TakesThePressPayloadItHasAlwaysAdvertised() =>
        Assert.Equal("PRESS", Assert.IsType<MqttButton>(
            MqttTestBed.Declared().Find(MqttEntityCatalog.ChargeToFull)).PayloadPress);

    [Fact]
    public void EveryWritableEntity_TakesCommandsAndEveryReadOnlyOneDoesNot()
    {
        string[] writable =
        [
            MqttEntityCatalog.SmartCharge, MqttEntityCatalog.ChargeStart, MqttEntityCatalog.ChargeStop,
            MqttEntityCatalog.ChargeToFull, MqttEntityCatalog.Preset,
            MqttEntityCatalog.KeepAwake, MqttEntityCatalog.KeepAwakeFor, MqttEntityCatalog.KeepAwakeDisplayOn,
            MqttEntityCatalog.LidDelay, MqttEntityCatalog.LidDelayTime, MqttEntityCatalog.LidDelayMinutes,
            MqttEntityCatalog.LidDischarge, MqttEntityCatalog.LidDischargePercent,
            MqttEntityCatalog.LidDelayLock,
            MqttEntityCatalog.LidDelayOffAfterSleep, MqttEntityCatalog.SmartStandby,
            MqttEntityCatalog.LowBatteryWarning, MqttEntityCatalog.LowBatteryLevel,
            MqttEntityCatalog.HighBatteryWarning, MqttEntityCatalog.HighBatteryLevel,
            MqttEntityCatalog.DrainWarning, MqttEntityCatalog.DrainRate,
            MqttEntityCatalog.NetworkProfiles, MqttEntityCatalog.UnknownNetworkPreset,
            MqttEntityCatalog.StartupDelay, MqttEntityCatalog.IconMode, MqttEntityCatalog.DowntimeGap,
        ];

        Assert.Equal(
            writable.Order(StringComparer.Ordinal),
            MqttTestBed.Declared().All.Where(e => e.IsCommand)
                       .Select(e => e.EntityId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EveryWritableEntity_WaitsForItsOwnWriteBeforeReportingIt()
    {
        // A command's own write and the read that reports it race otherwise, and the value published
        // is the one from before the command landed.
        foreach (var entity in MqttTestBed.Declared().All.Where(e => e.IsCommand && e.HasState))
            Assert.Equal(MqttConnection.ReflectDebounce, entity.Debounce);
    }

    [Fact]
    public void OnlyTheButton_HasNoStateTopic() =>
        Assert.Equal([MqttEntityCatalog.ChargeToFull],
                     MqttTestBed.Declared().All.Where(e => !e.HasState).Select(e => e.EntityId));

    [Fact]
    public void EveryPlatformButText_PublishesTheResetLiteralForAnAbsentReading()
    {
        // An empty payload is ignored on sensor, binary sensor, switch, number and select, and the
        // last value stands indefinitely. Only text takes an empty string as a value of its own.
        foreach (var entity in MqttTestBed.Declared().All.Where(e => e.HasState))
        {
            string? expected = entity.EntityId == MqttEntityCatalog.KeepAwakeFor ? null : MqttPayload.None;
            Assert.Equal(expected, entity.NoValuePayload);
        }
    }

    [Fact]
    public void WithNoReadingAtAll_EveryEntityButTheTextOnePublishesTheResetLiteral()
    {
        // Before the first battery tick and before the first surface read, both sources answer null.
        var set = MqttTestBed.Build(live: null, surface: null);

        foreach (var entity in set.All.Where(e => e.HasState))
        {
            string? expected = entity.EntityId == MqttEntityCatalog.KeepAwakeFor ? null : MqttPayload.None;
            Assert.Equal(expected, entity.ReadState());
        }
    }

    [Fact]
    public void AnEmptyAdapterReading_IsAnAbsentOneRatherThanAnEmptyPayload()
    {
        // The old shared payload omitted these keys when blank; a bare topic has to say so with the
        // reset literal, because an empty payload on a sensor changes nothing.
        var set = MqttTestBed.Build(
            surface: MqttTestBed.Surface(networkAlias: "", networkIp: null, networkAdapter: ""));

        Assert.Equal(MqttPayload.None, set.Find(MqttEntityCatalog.NetworkAdapterAlias)!.ReadState());
        Assert.Equal(MqttPayload.None, set.Find(MqttEntityCatalog.NetworkIpAddress)!.ReadState());
        Assert.Equal(MqttPayload.None, set.Find(MqttEntityCatalog.NetworkAdapterName)!.ReadState());
    }

    [Fact]
    public void MatchingNoNetworkProfile_IsAReadingRatherThanAnAbsentOne() =>
        Assert.Equal(SurfaceReader.NoProfile,
            MqttTestBed.Build(surface: MqttTestBed.Surface(matchedProfile: null))
                       .Find(MqttEntityCatalog.NetworkProfileMatched)!.ReadState());

    [Fact]
    public void TheReadings_AreFormattedForAMachineRatherThanForTheCurrentCulture()
    {
        var set = MqttTestBed.Build(
            MqttTestBed.Live(soc: 72, powerMw: 45_150, remainingMinutes: 25, adapterWatts: 65,
                             fullMwh: 51_450, designMwh: 57_000, chargeStart: 60, chargeStop: 80),
            MqttTestBed.Surface());

        Assert.Equal("72",   set.Find(MqttEntityCatalog.BatteryLevel)!.ReadState());
        Assert.Equal("45.2", set.Find(MqttEntityCatalog.BatteryPower)!.ReadState());
        Assert.Equal("25",   set.Find(MqttEntityCatalog.RemainingChargeTime)!.ReadState());
        Assert.Equal("65",   set.Find(MqttEntityCatalog.AdapterWatts)!.ReadState());
        Assert.Equal("51.5", set.Find(MqttEntityCatalog.CapacityFull)!.ReadState());
        Assert.Equal("57",   set.Find(MqttEntityCatalog.CapacityDesign)!.ReadState());
        Assert.Equal("60",   set.Find(MqttEntityCatalog.ChargeStart)!.ReadState());
        Assert.Equal("80",   set.Find(MqttEntityCatalog.ChargeStop)!.ReadState());
    }

    [Fact]
    public void ACapacityFirmwareHasNoFigureFor_ReadsAsAbsentRatherThanAsZero() =>
        Assert.Equal(MqttPayload.None,
            MqttTestBed.Build(MqttTestBed.Live(fullMwh: 0), MqttTestBed.Surface())
                       .Find(MqttEntityCatalog.CapacityFull)!.ReadState());

    [Fact]
    public void TheKeepAwakeExpiry_IsAFullInstantWithAnOffset() =>
        Assert.Equal("2026-08-25T17:30:00.0000000+02:00",
            MqttTestBed.Build(surface: MqttTestBed.Surface(
                keepAwakeExpires: new DateTimeOffset(2026, 8, 25, 17, 30, 0, TimeSpan.FromHours(2))))
                .Find(MqttEntityCatalog.KeepAwakeExpires)!.ReadState());

    [Fact]
    public void TheBooleanReadings_UseTheOnAndOffPayloadsTheDocumentDeclares()
    {
        var set = MqttTestBed.Build(
            MqttTestBed.Live(isCharging: true, onAc: false, smartChargeEnabled: true),
            MqttTestBed.Surface(keepAwake: false, travelOverride: true));

        Assert.Equal(MqttPayload.On,  set.Find(MqttEntityCatalog.IsCharging)!.ReadState());
        Assert.Equal(MqttPayload.Off, set.Find(MqttEntityCatalog.OnAc)!.ReadState());
        Assert.Equal(MqttPayload.On,  set.Find(MqttEntityCatalog.SmartCharge)!.ReadState());
        Assert.Equal(MqttPayload.Off, set.Find(MqttEntityCatalog.KeepAwake)!.ReadState());
        Assert.Equal(MqttPayload.On,  set.Find(MqttEntityCatalog.TravelOverride)!.ReadState());
    }

    [Fact]
    public void ThePowerState_TellsTheTwoMainsStatesApart()
    {
        // The reading neither is_charging nor on_ac gives on its own: a pack held at a charge limit
        // reads charging=off and on_ac=on, which is a state of its own rather than an absence.
        Assert.Equal("Charging", MqttTestBed.Build(MqttTestBed.Live(isCharging: true, onAc: true))
                                            .Find(MqttEntityCatalog.PowerState)!.ReadState());
        Assert.Equal("Idle on mains", MqttTestBed.Build(MqttTestBed.Live(isCharging: false, onAc: true))
                                                 .Find(MqttEntityCatalog.PowerState)!.ReadState());
        Assert.Equal("Discharging", MqttTestBed.Build(MqttTestBed.Live(isCharging: false, onAc: false))
                                               .Find(MqttEntityCatalog.PowerState)!.ReadState());
    }

    [Fact]
    public void ThePowerState_TakesNoCommand() =>
        // Read-only: the state is what the firmware is doing, and nothing on the broker changes it.
        Assert.IsType<MqttSensor>(MqttTestBed.Declared().Find(MqttEntityCatalog.PowerState));

    [Fact]
    public void TheTwoPresetSelects_OfferTheConfiguredPresetsAndOnlyOneOffersStayingPut()
    {
        var settings = new FakeSettingsActions { Presets = ["Daily", "Travel", "Storage"] };
        var set = MqttTestBed.Build(MqttTestBed.Live(), MqttTestBed.Surface(), settings: settings);

        Assert.Equal(["Daily", "Travel", "Storage"],
                     Assert.IsType<MqttSelect>(set.Find(MqttEntityCatalog.Preset)).Options());
        Assert.Equal([PresetEditValidator.UnknownNetworkSentinel, "Daily", "Travel", "Storage"],
                     Assert.IsType<MqttSelect>(set.Find(MqttEntityCatalog.UnknownNetworkPreset)).Options());
    }

    [Fact]
    public void TheTrayIconSelect_OffersTheEnumsOwnMemberNames() =>
        Assert.Equal(Enum.GetNames<TrayIconMode>(),
            Assert.IsType<MqttSelect>(MqttTestBed.Declared().Find(MqttEntityCatalog.IconMode)).Options());

    [Fact]
    public void TheDeclarations_NameNoLiveTopicTheyWouldEmpty() =>
        // Retiring a live entity's own config topic would delete and recreate it on every pass, and
        // lose the user's chosen entity id outright if anything claimed it in the gap. A retired
        // channel key is the same failure one subtree along: there is no component segment to keep it
        // off a live entity's state topic, so the key alone decides it. The publisher validates all
        // three at construction, where the only symptom is a throw at start-up.
        Assert.Null(DiscoveryDeclaration.Validate(
            MqttTestBed.Declared(), MqttEntityCatalog.Retired, MqttEntityCatalog.Migrating,
            MqttEntityCatalog.RetiredChannels));

    [Fact]
    public void TheMigratingList_IsTheFortyPreDocumentEntitiesAndNothingDeclaredSince() =>
        // The handover names the single-component config each entity already has on the broker, so a
        // pair that does not match a live entity would empty a topic nothing ever wrote to. An entity
        // declared after the document was adopted never had one, so it is absent here by design.
        Assert.Equal(
            MqttTestBed.Declared().All
                .Where(e => e.EntityId is not (MqttEntityCatalog.LowPowerMode or MqttEntityCatalog.PowerState
                                               or MqttEntityCatalog.LidDelayOffAfterSleep
                                               or MqttEntityCatalog.LidDelayTime
                                               or MqttEntityCatalog.LidDischarge
                                               or MqttEntityCatalog.LidDischargePercent))
                .Select(e => (e.Platform, e.EntityId)).Order(),
            MqttEntityCatalog.Migrating
                .Select(m => (m.Component, m.EntityId)).Order());

    [Fact]
    public void TheRetiredList_KeepsTheFiveConfigsAnEarlierEntitySetLeftBehind() =>
        Assert.Equal(
            [("sensor", "soc"), ("sensor", "power"), ("binary_sensor", "smart_charge"),
             ("sensor", "charge_start"), ("sensor", "charge_stop")],
            MqttEntityCatalog.Retired.Select(r => (r.Component, r.EntityId)));
}
