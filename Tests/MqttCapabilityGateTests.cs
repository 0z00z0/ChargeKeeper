using System;
using System.Linq;
using ChargeKeeper.Services;
using ZeroZero.Mqtt.Discovery;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// What decides whether an entity is announced when a reading is missing: a capability that could
/// not be read must not look like one that is absent, and an untrustworthy temperature announces
/// nothing rather than throwing.
/// </summary>
public class MqttCapabilityGateTests
{
    private static string[] Published(MqttEntitySet set) =>
        [.. set.Published(null).Select(e => e.EntityId).Order(StringComparer.Ordinal)];

    // ── A capability that could not be read ─────────────────────────────────────────────────────

    [Fact]
    public void AnEntityWhoseCapabilityCouldNotBeRead_KeepsWhateverTheRecordAlreadySaysAboutIt()
    {
        var set = MqttTestBed.Build(
            MqttTestBed.Live(), MqttTestBed.Surface(),
            capabilityReader: () => throw new TimeoutException("The controller did not answer."));

        var recorded = new PublishedDevice
        {
            DeviceId = "chargekeeper_office_x1",
            Entities =
            [
                new PublishedEntity { EntityId = MqttEntityCatalog.SmartCharge, Platform = "switch" },
                new PublishedEntity { EntityId = MqttEntityCatalog.LidDelay, Platform = "switch", Withheld = true },
            ],
        };

        var (published, withheld) = set.Resolve(null, recorded);

        Assert.Contains(MqttEntityCatalog.SmartCharge, published.Select(e => e.EntityId));
        Assert.Contains(MqttEntityCatalog.LidDelay, withheld.Select(e => e.EntityId));
    }

    // ── System temperature: issue #157's own gate ──────────────────────────────────────────────

    [Fact]
    public void AMachineWithNoTrustworthyReading_AnnouncesNeitherThermalEntityAndDoesNotThrow()
    {
        MqttEntitySet set = null!;
        var exception = Record.Exception(() =>
            set = MqttTestBed.Build(MqttTestBed.Live(), MqttTestBed.Surface(),
                                     systemTemperature: null, systemTemperatureMaximum: null));

        Assert.Null(exception);
        var published = Published(set);
        Assert.DoesNotContain(MqttEntityCatalog.SystemTemperature, published);
        Assert.DoesNotContain(MqttEntityCatalog.SystemTemperatureMaximum, published);
    }
}
