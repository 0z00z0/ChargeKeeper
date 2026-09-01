using System;
using System.Linq;
using ChargeKeeper.Services;
using ZeroZero.Mqtt.Discovery;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// What decides whether an entity is announced: the group toggle the user set, and the vendor gate
/// the hardware answers. And the third state neither of those has — a capability that could not be
/// read at all, which must not look like one that is absent.
/// </summary>
public class MqttCapabilityGateTests
{
    private static string[] Published(MqttEntitySet set) =>
        [.. set.Published(null).Select(e => e.EntityId).Order(StringComparer.Ordinal)];

    private static readonly string[] _smartChargeEntities =
    [
        MqttEntityCatalog.SmartCharge, MqttEntityCatalog.ChargeStart, MqttEntityCatalog.ChargeStop,
        MqttEntityCatalog.ChargeToFull, MqttEntityCatalog.Preset, MqttEntityCatalog.TravelOverride,
    ];

    private static readonly string[] _lidCloseEntities =
    [
        MqttEntityCatalog.LidDelay, MqttEntityCatalog.LidDelayTime, MqttEntityCatalog.LidDelayMinutes,
        MqttEntityCatalog.LidDischarge, MqttEntityCatalog.LidDischargePercent,
        MqttEntityCatalog.LidDelayLock, MqttEntityCatalog.LidDelayOffAfterSleep,
    ];

    private static MqttEntitySet WithCapabilities(PublishCapabilities capabilities) =>
        MqttTestBed.Build(MqttTestBed.Live(), MqttTestBed.Surface(), capabilities);

    // ── The vendor gates ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OnHardwareWithNumericThresholds_EveryEntityIsAnnounced() =>
        Assert.Equal(46, WithCapabilities(PublishCapabilities.Full).Published(null).Count);

    [Fact]
    public void OnHardwareWithNoChargeLimitInterface_NoSmartChargeEntityIsAnnounced()
    {
        var published = Published(WithCapabilities(
            PublishCapabilities.Full with { SmartCharge = SmartChargeSurface.Hidden }));

        foreach (string entityId in _smartChargeEntities)
            Assert.DoesNotContain(entityId, published);
    }

    [Fact]
    public void OnDiscreteBiosModes_OnlyTheOnOffSwitchSurvivesTheSmartChargeGate()
    {
        // The percentages, the preset picker and the one-shot override are all things such hardware
        // cannot honour, so announcing them would leave controls that silently do nothing.
        var published = Published(WithCapabilities(
            PublishCapabilities.Full with { SmartCharge = SmartChargeSurface.FixedModes }));

        Assert.Contains(MqttEntityCatalog.SmartCharge, published);
        foreach (string entityId in _smartChargeEntities.Where(e => e != MqttEntityCatalog.SmartCharge))
            Assert.DoesNotContain(entityId, published);
    }

    [Fact]
    public void WithNoLidCloseSupport_TheLidEntitiesGoButSmartStandbyStays()
    {
        var published = Published(WithCapabilities(
            PublishCapabilities.Full with { LidClose = false }));

        foreach (string entityId in _lidCloseEntities) Assert.DoesNotContain(entityId, published);
        Assert.Contains(MqttEntityCatalog.SmartStandby, published);
    }

    [Fact]
    public void SmartStandby_FollowsItsOwnGateRatherThanTheLidCloseOne()
    {
        var published = Published(WithCapabilities(
            PublishCapabilities.Full with { SmartStandby = false }));

        Assert.DoesNotContain(MqttEntityCatalog.SmartStandby, published);
        foreach (string entityId in _lidCloseEntities) Assert.Contains(entityId, published);
    }

    [Fact]
    public void ThePresetSelect_IsWithheldWhenThereIsNothingToOffer()
    {
        // An empty option list is not a schema a receiver accepts, and the state reverses the moment
        // a preset is saved — so it is withheld rather than announced empty.
        var set = MqttTestBed.Build(MqttTestBed.Live(), MqttTestBed.Surface(),
                                    settings: new FakeSettingsActions { Presets = [] });

        Assert.DoesNotContain(MqttEntityCatalog.Preset, Published(set));
        // The unknown-network picker always has the sentinel, so it stands whatever the preset list says.
        Assert.Contains(MqttEntityCatalog.UnknownNetworkPreset, Published(set));
    }

    // ── A capability that could not be read ─────────────────────────────────────────────────────

    [Fact]
    public void AVendorReadThatThrows_IsUnknownRatherThanAbsent()
    {
        // An EC that does not answer and a WMI call that times out both look like "no such capability"
        // to a predicate that can only return a boolean. They are not, and a resume from standby is
        // exactly when they happen.
        var set = MqttTestBed.Build(
            MqttTestBed.Live(), MqttTestBed.Surface(),
            capabilityReader: () => throw new TimeoutException("The controller did not answer."));

        foreach (string entityId in _smartChargeEntities.Concat(_lidCloseEntities))
            Assert.Null(set.Find(entityId)!.IsPublished(null));
    }

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

    [Fact]
    public void AnEntityTheRecordHasNeverHeardOf_IsLeftOutRatherThanAnnouncedOnAFailedRead()
    {
        var set = MqttTestBed.Build(
            MqttTestBed.Live(), MqttTestBed.Surface(),
            capabilityReader: () => throw new TimeoutException("The controller did not answer."));

        var (published, withheld) = set.Resolve(null, recorded: null);

        foreach (string entityId in _smartChargeEntities.Concat(_lidCloseEntities))
        {
            Assert.DoesNotContain(entityId, published.Select(e => e.EntityId));
            Assert.DoesNotContain(entityId, withheld.Select(e => e.EntityId));
        }
    }

    [Fact]
    public void ASwitchedOffGroup_AnswersFalseWithoutTheHardwareBeingReadAtAll()
    {
        // There is nothing to publish either way, and reading a controller for an entity nobody wants
        // is work for its own sake — on the very interface most likely to be slow.
        int reads = 0;
        var set = MqttTestBed.Build(
            MqttTestBed.Live(), MqttTestBed.Surface(),
            capabilityReader: () => { reads++; return PublishCapabilities.Full; });

        var groups = MqttTestBed.Groups((MqttPublishGroups.SmartCharge, false));

        Assert.False(set.Find(MqttEntityCatalog.ChargeStart)!.IsPublished(groups));
        Assert.Equal(0, reads);
    }

    // ── The group toggles ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void SwitchingAGroupOff_WithholdsExactlyItsOwnEntities()
    {
        var set = MqttTestBed.Declared();
        // App diagnostics is off by default, so it is switched on here to leave exactly one group off.
        var groups = MqttTestBed.Groups(
            (MqttPublishGroups.AppDiagnostics, true), (MqttPublishGroups.Network, false));

        string[] networkEntities =
        [
            MqttEntityCatalog.NetworkProfiles, MqttEntityCatalog.UnknownNetworkPreset,
            MqttEntityCatalog.NetworkAdapterAlias, MqttEntityCatalog.NetworkIpAddress,
            MqttEntityCatalog.NetworkAdapterName, MqttEntityCatalog.NetworkProfileMatched,
        ];

        Assert.Equal(
            networkEntities.Order(StringComparer.Ordinal),
            set.Withheld(groups).Select(e => e.EntityId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AFreshInstallation_AnnouncesEveryGroupButAppDiagnostics()
    {
        // Nothing has been toggled, so each key takes its own declared default.
        var groups = MqttTestBed.Groups();
        var withheld = MqttTestBed.Declared().Withheld(groups).Select(e => e.EntityId).ToArray();

        string[] diagnostics =
        [
            MqttEntityCatalog.AppVersion, MqttEntityCatalog.StartupDelay,
            MqttEntityCatalog.IconMode, MqttEntityCatalog.DowntimeGap,
        ];

        Assert.Equal(diagnostics.Order(StringComparer.Ordinal), withheld.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void EveryEntity_CarriesOneOfTheDeclaredGroupKeys()
    {
        // A key nothing declares is announced regardless, so a typo would publish an entity no
        // settings row can ever switch off.
        var declared = MqttPublishGroups.Declared.Select(g => g.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var entity in MqttTestBed.Declared().All)
            Assert.Contains(entity.Group!, declared);
    }
}
