using System.Text.Json;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>Records what a settings command asked for, so the routing can be checked without writing
/// settings.json, driving the power scheme or reaching a vendor service.</summary>
internal sealed class HaSettingsSpy : IHaSettingsActions
{
    public List<string> Calls { get; } = [];
    public List<string> Presets { get; set; } = ["Daily", "Travel"];
    public KeepAwakeRequest? Started;
    public string? UnknownPreset;
    public TrayIconMode? Icon;

    public IReadOnlyList<string> PresetNames() => Presets;

    private void Note(string what, object value) => Calls.Add($"{what}={value}");

    public void SetKeepAwake(bool on)               => Note(nameof(SetKeepAwake), on);
    public void StartKeepAwake(KeepAwakeRequest r)  { Started = r; Note(nameof(StartKeepAwake), r.Kind); }
    public void SetKeepAwakeDisplayOn(bool on)      => Note(nameof(SetKeepAwakeDisplayOn), on);
    public void SetLidDelay(bool on)                => Note(nameof(SetLidDelay), on);
    public void SetLidDelayMinutes(int m)           => Note(nameof(SetLidDelayMinutes), m);
    public void SetLidDelayLock(bool on)            => Note(nameof(SetLidDelayLock), on);
    public void SetSmartStandby(bool on)            => Note(nameof(SetSmartStandby), on);
    public void SetLowBatteryWarning(bool on)       => Note(nameof(SetLowBatteryWarning), on);
    public void SetLowBatteryLevel(int p)           => Note(nameof(SetLowBatteryLevel), p);
    public void SetHighBatteryWarning(bool on)      => Note(nameof(SetHighBatteryWarning), on);
    public void SetHighBatteryLevel(int p)          => Note(nameof(SetHighBatteryLevel), p);
    public void SetDrainWarning(bool on)            => Note(nameof(SetDrainWarning), on);
    public void SetDrainRate(int p)                 => Note(nameof(SetDrainRate), p);
    public void SetNetworkProfiles(bool on)         => Note(nameof(SetNetworkProfiles), on);
    public void SetUnknownNetworkPreset(string? n)  { UnknownPreset = n; Note(nameof(SetUnknownNetworkPreset), n ?? "<null>"); }
    public void SetStartupDelay(int s)              => Note(nameof(SetStartupDelay), s);
    public void SetIconMode(TrayIconMode m)         { Icon = m; Note(nameof(SetIconMode), m); }
    public void SetDowntimeGap(int m)               => Note(nameof(SetDowntimeGap), m);
}

/// <summary>A minimal charge-control sink; the routing of the charge commands has its own tests.</summary>
internal sealed class HaChargeSpy : IChargeControlActions
{
    public (int Start, int Stop) CurrentThresholds() => (60, 80);
    public void ApplyThresholds(int start, int stop) { }
    public void SetSmartChargeEnabled(bool enable) { }
    public void ChargeToFullOnce() { }
    public void ApplyPreset(string name) { }
}

// The published surface: which entities a configuration announces, which it withdraws, how a write
// from the broker is validated, and what never reaches a payload. No broker.
public class HaEntitySurfaceTests
{
    private const string Node   = "chargekeeper_pc";
    private const string Prefix = "homeassistant";
    private static readonly string[] Presets = ["Daily", "Travel"];

    private static IReadOnlyList<HaEntity> Announce(HaCategorySet c, HaCapabilities k) =>
        HaEntityCatalog.Announce(c, k);

    private static HaCategorySet AllBut(HaCategory off) => new(
        off != HaCategory.BatteryStatus, off != HaCategory.SmartCharge, off != HaCategory.KeepAwake,
        off != HaCategory.LidClose, off != HaCategory.Notifications, off != HaCategory.Network,
        off != HaCategory.AppDiagnostics);

    // Every group on

    [Fact]
    public void EveryCategoryOn_AnnouncesTheWholeCatalogue_AndWithholdsNothing()
    {
        var announced = Announce(HaCategorySet.All, HaCapabilities.Full);

        Assert.Equal(HaEntityCatalog.All.Count, announced.Count);
        Assert.Empty(HaEntityCatalog.Withheld(HaCategorySet.All, HaCapabilities.Full));
    }

    [Fact]
    public void EveryCategoryOn_CoversEveryGroup_WithNoDuplicateObjectIds()
    {
        var announced = Announce(HaCategorySet.All, HaCapabilities.Full);

        foreach (HaCategory category in Enum.GetValues<HaCategory>())
            Assert.Contains(announced, e => e.Category == category);
        // The object id is the topic segment and the unique_id stem, so a collision would make two
        // entities share one entity in the consumer.
        Assert.Equal(announced.Count, announced.Select(e => e.ObjectId).Distinct().Count());
    }

    [Fact]
    public void Network_PublishesTheResolvedAdaptersAliasAddressAndName_AndTheMatchedProfile()
    {
        var ids = Announce(HaCategorySet.All, HaCapabilities.Full)
            .Where(e => e.Category == HaCategory.Network).Select(e => e.ObjectId).ToList();

        Assert.Contains(HaEntityCatalog.NetworkAdapterAlias, ids);
        Assert.Contains(HaEntityCatalog.NetworkIpAddress, ids);
        Assert.Contains(HaEntityCatalog.NetworkAdapterName, ids);
        Assert.Contains(HaEntityCatalog.NetworkProfileMatched, ids);
    }

    [Fact]
    public void EntityCategory_FilesSettingsAsConfigAndReadingsAsDiagnostic_LeavingPrimariesBare()
    {
        var configs = HaDiscovery.DiscoveryConfigs(
            Node, Prefix, "ChargeKeeper (PC)", "1.4.0", Presets,
            Announce(HaCategorySet.All, HaCapabilities.Full)).ToList();

        static string? CategoryOf(List<(string Topic, string Json)> all, string component, string objectId)
        {
            var (_, json) = all.Single(c => c.Topic == $"{Prefix}/{component}/{Node}/{objectId}/config");
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("entity_category", out var v) ? v.GetString() : null;
        }

        Assert.Equal("config", CategoryOf(configs, "number", HaEntityCatalog.LowBatteryLevel));
        Assert.Equal("diagnostic", CategoryOf(configs, "sensor", HaEntityCatalog.AppVersion));
        // A primary control carries no entity_category at all, which is what keeps it on the main card.
        Assert.Null(CategoryOf(configs, "switch", HaEntityCatalog.SmartCharge));
        Assert.Null(CategoryOf(configs, "sensor", HaEntityCatalog.BatteryLevel));
    }

    [Fact]
    public void UnknownNetworkPresetSelect_OffersTheDoNothingSentinelAlongsideThePresets()
    {
        var (_, json) = HaDiscovery.DiscoveryConfigs(
                Node, Prefix, "ChargeKeeper (PC)", "1.4.0", Presets,
                Announce(HaCategorySet.All, HaCapabilities.Full))
            .Single(c => c.Topic == $"{Prefix}/select/{Node}/{HaEntityCatalog.UnknownNetworkPreset}/config");

        using var doc = JsonDocument.Parse(json);
        var options = doc.RootElement.GetProperty("options").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Equal(PresetEditValidator.UnknownNetworkSentinel, options[0]);
        Assert.Equal(Presets, options[1..]);
    }

    // One group off

    // The group arrives as an int: xUnit needs a public signature, and the enum is internal.
    [Theory]
    [InlineData((int)HaCategory.BatteryStatus)]
    [InlineData((int)HaCategory.SmartCharge)]
    [InlineData((int)HaCategory.KeepAwake)]
    [InlineData((int)HaCategory.LidClose)]
    [InlineData((int)HaCategory.Notifications)]
    [InlineData((int)HaCategory.Network)]
    [InlineData((int)HaCategory.AppDiagnostics)]
    public void CategoryOff_AnnouncesNoneOfIts_AndWithholdsExactlyThose(int category)
    {
        var off        = (HaCategory)category;
        var categories = AllBut(off);
        var announced  = Announce(categories, HaCapabilities.Full);
        var withheld   = HaEntityCatalog.Withheld(categories, HaCapabilities.Full);

        Assert.DoesNotContain(announced, e => e.Category == off);
        Assert.All(withheld, e => Assert.Equal(off, e.Category));
        Assert.Equal(HaEntityCatalog.All.Count, announced.Count + withheld.Count);
        Assert.NotEmpty(withheld);
    }

    [Fact]
    public void CategoryOff_EmitsARemovalTopicPerWithheldEntity_AndNoneForTheAnnouncedOnes()
    {
        var categories = AllBut(HaCategory.Notifications);
        var withheld   = HaEntityCatalog.Withheld(categories, HaCapabilities.Full);

        var removals = HaDiscovery.RemovalTopics(Node, Prefix, withheld).ToList();

        // The discovery convention deletes a component when its config topic is emptied, so a removal
        // is the same topic the announcement used — nothing else evicts it.
        Assert.Equal(withheld.Count, removals.Count);
        foreach (var e in withheld)
            Assert.Contains($"{Prefix}/{e.Component}/{Node}/{e.ObjectId}/config", removals);

        var announcedTopics = HaDiscovery.DiscoveryConfigs(
            Node, Prefix, "ChargeKeeper (PC)", "1.4.0", Presets,
            Announce(categories, HaCapabilities.Full)).Select(c => c.Topic).ToList();
        Assert.Empty(removals.Intersect(announcedTopics));
    }

    [Fact]
    public void CategoryBackOn_AnnouncesItAgain_AndStopsRemovingIt()
    {
        var off = AllBut(HaCategory.KeepAwake);
        string keepAwakeConfig = $"{Prefix}/switch/{Node}/{HaEntityCatalog.KeepAwake}/config";

        Assert.Contains(keepAwakeConfig,
            HaDiscovery.RemovalTopics(Node, Prefix, HaEntityCatalog.Withheld(off, HaCapabilities.Full)));
        Assert.Contains(keepAwakeConfig, HaDiscovery.DiscoveryConfigs(
            Node, Prefix, "ChargeKeeper (PC)", "1.4.0", Presets,
            Announce(HaCategorySet.All, HaCapabilities.Full)).Select(c => c.Topic));
    }

    [Fact]
    public void TopicsToClear_StillCoversTheWholeCatalogue_WhateverIsAnnounced()
    {
        // A node being abandoned sheds everything it ever owned, not just what it announces now.
        var topics = HaDiscovery.TopicsToClear(Node, Prefix).ToList();
        foreach (var e in HaEntityCatalog.All)
            Assert.Contains($"{Prefix}/{e.Component}/{Node}/{e.ObjectId}/config", topics);
        Assert.Contains(HaDiscovery.StatusTopic(Node), topics);
    }

    // Capability gates

    [Fact]
    public void NoChargeLimitInterface_AnnouncesNoSmartChargeEntityAtAll()
    {
        var caps = HaCapabilities.Full with { SmartCharge = SmartChargeSurface.Hidden };
        var announced = Announce(HaCategorySet.All, caps);

        Assert.DoesNotContain(announced, e => e.Category == HaCategory.SmartCharge);
        // Withheld, not merely unannounced: the retained config must be emptied on such a machine too.
        Assert.Contains(HaEntityCatalog.Withheld(HaCategorySet.All, caps),
                        e => e.ObjectId == HaEntityCatalog.SmartCharge);
    }

    [Fact]
    public void FixedModeHardware_KeepsTheSwitch_ButNotThePercentagesPresetsOrOverride()
    {
        var caps = HaCapabilities.Full with { SmartCharge = SmartChargeSurface.FixedModes };
        var ids = Announce(HaCategorySet.All, caps)
            .Where(e => e.Category == HaCategory.SmartCharge).Select(e => e.ObjectId).ToList();

        Assert.Equal([HaEntityCatalog.SmartCharge], ids);
    }

    [Fact]
    public void NoLid_WithdrawsTheLidEntities_ButLeavesSmartStandbyOnItsOwnGate()
    {
        var caps = HaCapabilities.Full with { LidClose = false };
        var ids = Announce(HaCategorySet.All, caps)
            .Where(e => e.Category == HaCategory.LidClose).Select(e => e.ObjectId).ToList();

        Assert.Equal([HaEntityCatalog.SmartStandby], ids);
    }

    [Fact]
    public void NoStandbyScheduling_WithdrawsOnlySmartStandby()
    {
        var caps = HaCapabilities.Full with { SmartStandby = false };
        var ids = Announce(HaCategorySet.All, caps)
            .Where(e => e.Category == HaCategory.LidClose).Select(e => e.ObjectId).ToList();

        Assert.DoesNotContain(HaEntityCatalog.SmartStandby, ids);
        Assert.Contains(HaEntityCatalog.LidDelay, ids);
    }

    [Fact]
    public void CategoryOnButIncapable_StillYieldsARemovalPayload()
    {
        // The group is on, so nothing but the gate withholds these; without the removal they would
        // linger from a run on capable hardware, or from a firmware that stopped answering.
        var caps = HaCapabilities.Full with { SmartCharge = SmartChargeSurface.Hidden };
        var removals = HaDiscovery.RemovalTopics(
            Node, Prefix, HaEntityCatalog.Withheld(HaCategorySet.All, caps)).ToList();

        Assert.Contains($"{Prefix}/number/{Node}/{HaEntityCatalog.ChargeStart}/config", removals);
        Assert.Contains($"{Prefix}/select/{Node}/{HaEntityCatalog.Preset}/config", removals);
    }

    // Inbound writes

    [Theory]
    // Each bound is the one the Settings page enforces, so a remote write reaches nothing the UI cannot.
    [InlineData(HaEntityCatalog.LowBatteryLevel, "4")]
    [InlineData(HaEntityCatalog.LowBatteryLevel, "51")]
    [InlineData(HaEntityCatalog.HighBatteryLevel, "59")]
    [InlineData(HaEntityCatalog.DrainRate, "11")]
    [InlineData(HaEntityCatalog.LidDelayMinutes, "0")]
    [InlineData(HaEntityCatalog.LidDelayMinutes, "241")]
    [InlineData(HaEntityCatalog.StartupDelay, "-1")]
    [InlineData(HaEntityCatalog.DowntimeGap, "61")]
    [InlineData(HaEntityCatalog.StartupDelay, "soon")]
    [InlineData(HaEntityCatalog.IconMode, "Rectangle")]
    [InlineData(HaEntityCatalog.KeepAwakeFor, "later")]
    [InlineData(HaEntityCatalog.KeepAwakeFor, "25:00")]
    [InlineData(HaEntityCatalog.KeepAwakeDisplayOn, "maybe")]
    public void OutOfRangeOrUnparseableWrite_IsRefused_AndNothingIsApplied(string objectId, string payload)
    {
        Assert.False(HaCommand.TryParse(objectId, payload, out _));
    }

    [Theory]
    [InlineData(HaEntityCatalog.LowBatteryLevel, "5")]
    [InlineData(HaEntityCatalog.LowBatteryLevel, "50")]
    [InlineData(HaEntityCatalog.HighBatteryLevel, "80.0")]   // HA number entities may publish a float
    [InlineData(HaEntityCatalog.LidDelayMinutes, "240")]
    [InlineData(HaEntityCatalog.StartupDelay, "0")]
    [InlineData(HaEntityCatalog.DowntimeGap, "0")]
    [InlineData(HaEntityCatalog.IconMode, "numeric")]        // the select's own options, case-insensitively
    public void InRangeWrite_IsAccepted(string objectId, string payload)
    {
        Assert.True(HaCommand.TryParse(objectId, payload, out _));
    }

    [Fact]
    public void KeepAwakeText_IsParsedByTheSameParserTheSettingsBoxUses()
    {
        Assert.True(HaCommand.TryParse(HaEntityCatalog.KeepAwakeFor, "1h30", out var cmd));
        Assert.Equal(KeepAwakeKind.Duration, cmd.Request!.Kind);
        Assert.Equal(TimeSpan.FromMinutes(90), cmd.Request.Duration);

        var spy = new HaSettingsSpy();
        Assert.True(HaCommandDispatcher.Dispatch(cmd, new HaChargeSpy(), spy));
        Assert.Equal(TimeSpan.FromMinutes(90), spy.Started!.Duration);
    }

    [Fact]
    public void RefusedWrite_NeverReachesAnAction()
    {
        var spy = new HaSettingsSpy();
        foreach (string payload in new[] { "4", "51", "not a number" })
        {
            // Exactly what the live receive path does: parse, and dispatch only on success. A refusal
            // is final because there is no second, looser road to the setting.
            if (HaCommand.TryParse(HaEntityCatalog.LowBatteryLevel, payload, out var cmd))
                HaCommandDispatcher.Dispatch(cmd, new HaChargeSpy(), spy);
        }
        Assert.Empty(spy.Calls);
    }

    [Fact]
    public void UnknownNetworkPreset_AcceptsAConfiguredNameOrTheSentinel_AndRefusesAnythingElse()
    {
        var spy = new HaSettingsSpy();
        Assert.True(HaCommand.TryParse(HaEntityCatalog.UnknownNetworkPreset, "Travel", out var known));
        Assert.True(HaCommandDispatcher.Dispatch(known, new HaChargeSpy(), spy));
        Assert.Equal("Travel", spy.UnknownPreset);

        Assert.True(HaCommand.TryParse(HaEntityCatalog.UnknownNetworkPreset,
                                       PresetEditValidator.UnknownNetworkSentinel, out var sentinel));
        Assert.True(HaCommandDispatcher.Dispatch(sentinel, new HaChargeSpy(), spy));
        Assert.Null(spy.UnknownPreset);   // "route nowhere" is stored as no preset at all

        Assert.True(HaCommand.TryParse(HaEntityCatalog.UnknownNetworkPreset, "Nowhere", out var unknown));
        Assert.False(HaCommandDispatcher.Dispatch(unknown, new HaChargeSpy(), spy));
    }

    [Fact]
    public void EveryWritableEntity_HasACommandTopicAndAParserThatAcceptsIt()
    {
        var configs = HaDiscovery.DiscoveryConfigs(
            Node, Prefix, "ChargeKeeper (PC)", "1.4.0", Presets,
            Announce(HaCategorySet.All, HaCapabilities.Full)).ToList();

        foreach (var e in HaEntityCatalog.All.Where(e => e.IsCommand))
        {
            var (_, json) = configs.Single(c => c.Topic == $"{Prefix}/{e.Component}/{Node}/{e.ObjectId}/config");
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(HaDiscovery.CommandTopic(Node, e.ObjectId),
                         doc.RootElement.GetProperty("command_topic").GetString());
            // An entity the consumer can write to but the parser does not know is a dead control.
            Assert.True(HaCommand.TryParse(e.ObjectId, SamplePayloadFor(e), out _),
                        $"{e.ObjectId} advertises a command topic but refuses its own sample payload.");
        }
    }

    private static string SamplePayloadFor(HaEntity e) => e.Component switch
    {
        "switch" => "ON",
        "button" => HaCommand.ButtonPress,
        "number" => NumberSampleFor(e),
        "text"   => "30m",
        _        => e.ObjectId == HaEntityCatalog.IconMode ? nameof(TrayIconMode.Arc) : "Daily",
    };

    // The declared minimum is by definition in range, so it exercises the parser without a second
    // table of magic values to keep in step.
    private static string NumberSampleFor(HaEntity e) =>
        ((int)e.Extra["min"]).ToString(System.Globalization.CultureInfo.InvariantCulture);

    // Secrets

    [Fact]
    public void NoBrokerCredentialAppearsInAnyPublishedPayload()
    {
        const string user = "mqtt-user-sentinel";
        const string pass = "mqtt-pass-sentinel";
        const string host = "broker-host-sentinel";

        var settings = new AppSettings
        {
            MqttUsername = user, MqttPassword = pass, MqttBrokerHost = host,
            MqttDiscoveryPrefix = Prefix, MqttNodeId = Node,
        };

        // Everything the app ever puts on the wire: every discovery config, the battery payload and
        // the settings payload. Scanned whole, so a leak through any field is caught, not just the
        // fields anyone thought to check.
        var payloads = new List<string>();
        payloads.AddRange(HaDiscovery.DiscoveryConfigs(
            Node, Prefix, "ChargeKeeper (PC)", "1.4.0", Presets,
            Announce(HaCategorySet.All, HaCapabilities.Full)).Select(c => c.Json));
        payloads.Add(HaDiscovery.StatePayload(new HaState(
            72, HaDiscovery.StateCharging, false, 45000, true, true, "Good", 40, true, 60, 80, 65,
            "Daily", 56000, 60000)));
        payloads.Add(HaSurfacePayload.Build(HaSurfaceReader.From(
            settings, session: null,
            new NetworkLocation("aa-bb-cc-dd-ee-ff", "10.0.0.0/24", true, "Wi-Fi"),
            new NetworkAdapterInfo("Wi-Fi", "10.0.0.42", "Intel Wi-Fi 6E AX211"),
            standbyRunning: true, appVersion: "1.4.0")));

        foreach (string payload in payloads)
        {
            Assert.DoesNotContain(user, payload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(pass, payload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(host, payload, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void NoEntityIsNamedAfterACredentialField()
    {
        // The scan above proves no value leaks; this proves no entity was ever built to carry one.
        foreach (var e in HaEntityCatalog.All)
        {
            Assert.DoesNotContain("password", e.ObjectId, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("username", e.ObjectId, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("credential", e.ObjectId, StringComparison.OrdinalIgnoreCase);
        }
    }

    // The settings payload

    [Fact]
    public void SurfacePayload_CarriesEveryFieldItsEntitiesRead()
    {
        var surface = HaSurfaceReader.From(
            new AppSettings { LowBatteryWarningPct = 20, StartupDelaySeconds = 5, IconMode = TrayIconMode.Numeric },
            session: null, default,
            new NetworkAdapterInfo("Wi-Fi", "10.0.0.42", "Intel Wi-Fi 6E AX211"),
            standbyRunning: false, appVersion: "1.4.0");

        using var doc = JsonDocument.Parse(HaSurfacePayload.Build(surface));
        var root = doc.RootElement;
        Assert.Equal(20, root.GetProperty("low_battery_level").GetInt32());
        Assert.Equal(5, root.GetProperty("startup_delay").GetInt32());
        Assert.Equal("Numeric", root.GetProperty("icon_mode").GetString());
        Assert.Equal("Wi-Fi", root.GetProperty("network_alias").GetString());
        Assert.Equal("10.0.0.42", root.GetProperty("network_ip").GetString());
        Assert.Equal("Intel Wi-Fi 6E AX211", root.GetProperty("network_adapter").GetString());
        // No rule matched, which is a known reading rather than an unknown one.
        Assert.Equal(HaSurfacePayload.NoProfile, root.GetProperty("network_profile").GetString());
        // No session, so no expiry — the entity reads "unknown" rather than a fabricated instant.
        Assert.False(root.TryGetProperty("keep_awake_expires", out _));
    }

    [Fact]
    public void SurfacePayload_MatchedProfile_IsTheRulesOwnName()
    {
        var settings = new AppSettings
        {
            NetworkLocationRules = [new NetworkLocationRule { Name = "Home", IpCidr = "10.0.0.0/24" }],
        };
        var surface = HaSurfaceReader.From(
            settings, session: null,
            new NetworkLocation("aa-bb-cc-dd-ee-ff", "10.0.0.0/24", true, "Ethernet"),
            new NetworkAdapterInfo("Ethernet", "10.0.0.42", "Realtek GbE"),
            standbyRunning: false, appVersion: "1.4.0");

        using var doc = JsonDocument.Parse(HaSurfacePayload.Build(surface));
        Assert.Equal("Home", doc.RootElement.GetProperty("network_profile").GetString());
    }

    [Fact]
    public void SurfaceEntities_ReadTheStatusTopic_AndLiveOnesTheStateTopic()
    {
        var configs = HaDiscovery.DiscoveryConfigs(
            Node, Prefix, "ChargeKeeper (PC)", "1.4.0", Presets,
            Announce(HaCategorySet.All, HaCapabilities.Full)).ToList();

        foreach (var e in HaEntityCatalog.All)
        {
            var (_, json) = configs.Single(c => c.Topic == $"{Prefix}/{e.Component}/{Node}/{e.ObjectId}/config");
            using var doc = JsonDocument.Parse(json);
            bool hasTopic = doc.RootElement.TryGetProperty("state_topic", out var topic);
            switch (e.State)
            {
                case HaStateSource.None:    Assert.False(hasTopic); break;
                case HaStateSource.Live:    Assert.Equal(HaDiscovery.StateTopic(Node), topic.GetString()); break;
                default:                    Assert.Equal(HaDiscovery.StatusTopic(Node), topic.GetString()); break;
            }
        }
    }

    [Fact]
    public void StatePayload_CarriesTheCapacityReadings_OnlyWhenTheBatteryReportsThem()
    {
        var known = HaDiscovery.StatePayload(new HaState(
            72, HaDiscovery.StateCharging, false, 45000, true, true, "Good", 40, true, 60, 80, 65,
            "Daily", 56000, 60000));
        using (var doc = JsonDocument.Parse(known))
        {
            Assert.Equal(56000, doc.RootElement.GetProperty("capacity_full_mwh").GetInt32());
            Assert.Equal(60000, doc.RootElement.GetProperty("capacity_design_mwh").GetInt32());
        }

        var unknown = HaDiscovery.StatePayload(new HaState(
            72, HaDiscovery.StateCharging, false, 45000, true, true, null, null, true, null, 100, null,
            null));
        using (var doc = JsonDocument.Parse(unknown))
        {
            Assert.False(doc.RootElement.TryGetProperty("capacity_full_mwh", out _));
            Assert.False(doc.RootElement.TryGetProperty("capacity_design_mwh", out _));
        }
    }
}
