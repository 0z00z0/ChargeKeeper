using System;
using System.Collections.Generic;
using System.Linq;
using ChargeKeeper.Services;
using ZeroZero.Mqtt;
using ZeroZero.Mqtt.Discovery;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The published surface as a declaration: the component mix, the device id every unique_id starts
/// with, the topics the declarations may not empty, and the culture the readings are written in.
/// </summary>
/// <remarks>
/// An entity id or its component composes an installation's record in Home Assistant, so changing one
/// silently discards the name, the entity id, the area, the labels and every automation a user
/// attached to that entity — and there is no recovery.
/// </remarks>
public class MqttEntityCatalogTests
{
    [Fact]
    public void TheEntityMix_IsTwentyFourSensorsThirteenSwitchesTenNumbersFourBinaryThreeSelectsTwoButtonsAndAText()
    {
        var byPlatform = MqttTestBed.Declared().All
            .GroupBy(e => e.Platform)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        Assert.Equal(
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["sensor"] = 24, ["switch"] = 13, ["number"] = 10,
                ["binary_sensor"] = 4, ["select"] = 3, ["button"] = 2, ["text"] = 1,
            },
            byPlatform);
    }

    [Fact]
    public void TheDefaultDeviceId_IsTheNodeIdTheAppHasAlwaysPublishedUnder() =>
        // The unique_id is <deviceId>_<entityId>, so an installation only keeps its entities if the
        // module derives the same id from the machine name that HaDiscovery.NodeId used to.
        Assert.Equal("chargekeeper_office_x1",
                     MqttIdentity.Default(MqttPublisher.TopicRoot, "Office-X1"));

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
}
