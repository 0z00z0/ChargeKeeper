using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ChargeKeeper.Services;
using ZeroZero.Mqtt;
using ZeroZero.Mqtt.Discovery;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The naming policy, and the identifiers a rename must never move with it.
/// </summary>
/// <remarks>
/// <para>Home Assistant keys its entity registry on the <c>unique_id</c>, so an installation keeps
/// its entity ids, areas, labels and automations across a display-name change — but only for as
/// long as the two stay unrelated. <see cref="EveryUniqueId_IsTheOneAnInstallationAlreadyHas"/>
/// reads the identifiers out of the composed discovery document and compares them against literals
/// written out here, so an identifier derived from a name, or an entity id edited alongside a name,
/// fails rather than shipping.</para>
/// <para>The literals are spelled in full rather than composed from
/// <see cref="MqttEntityCatalog"/>'s constants: a test built from the same constants as the code
/// would follow a changed constant instead of catching it.</para>
/// </remarks>
public class MqttEntityNamingTests
{
    /// <summary>A fixed device id, so the expected identifiers are literals rather than a formula.</summary>
    private const string DeviceId = "chargekeeper_office_x1";

    /// <summary>Every <c>unique_id</c> an installation already holds, in the order the catalogue
    /// declares them. <b>Frozen.</b> An entry changes only when an entity is added or removed.</summary>
    private static readonly string[] _uniqueIds =
    [
        "chargekeeper_office_x1_battery_level",
        "chargekeeper_office_x1_battery_state",
        "chargekeeper_office_x1_battery_power",
        "chargekeeper_office_x1_is_charging",
        "chargekeeper_office_x1_on_ac",
        "chargekeeper_office_x1_power_state",
        "chargekeeper_office_x1_battery_health",
        "chargekeeper_office_x1_remaining_charge_time",
        "chargekeeper_office_x1_adapter_watts",
        "chargekeeper_office_x1_capacity_full",
        "chargekeeper_office_x1_capacity_design",
        "chargekeeper_office_x1_low_power_mode",
        "chargekeeper_office_x1_smart_charge",
        "chargekeeper_office_x1_charge_start",
        "chargekeeper_office_x1_charge_stop",
        "chargekeeper_office_x1_charge_to_full",
        "chargekeeper_office_x1_preset",
        "chargekeeper_office_x1_travel_override",
        "chargekeeper_office_x1_keep_awake",
        "chargekeeper_office_x1_keep_awake_for",
        "chargekeeper_office_x1_keep_awake_expires",
        "chargekeeper_office_x1_keep_awake_display_on",
        "chargekeeper_office_x1_lid_delay",
        "chargekeeper_office_x1_lid_delay_time",
        "chargekeeper_office_x1_lid_delay_minutes",
        "chargekeeper_office_x1_lid_discharge",
        "chargekeeper_office_x1_lid_discharge_percent",
        "chargekeeper_office_x1_lid_delay_lock",
        "chargekeeper_office_x1_lid_delay_off_after_sleep",
        "chargekeeper_office_x1_smart_standby",
        "chargekeeper_office_x1_low_battery_warning",
        "chargekeeper_office_x1_low_battery_level",
        "chargekeeper_office_x1_high_battery_warning",
        "chargekeeper_office_x1_high_battery_level",
        "chargekeeper_office_x1_drain_warning",
        "chargekeeper_office_x1_drain_rate",
        "chargekeeper_office_x1_network_profiles",
        "chargekeeper_office_x1_unknown_network_preset",
        "chargekeeper_office_x1_network_adapter_alias",
        "chargekeeper_office_x1_network_ip_address",
        "chargekeeper_office_x1_network_adapter_name",
        "chargekeeper_office_x1_network_profile",
        "chargekeeper_office_x1_app_version",
        "chargekeeper_office_x1_startup_delay",
        "chargekeeper_office_x1_icon_mode",
        "chargekeeper_office_x1_downtime_gap",
    ];

    /// <summary>The identifiers as a receiver reads them: out of a composed document, rather than
    /// re-derived from the table the document was built from.</summary>
    private static List<string> PublishedUniqueIds()
    {
        string json = DiscoveryDocument.Build(
            MqttPublisher.TopicRoot,
            new MqttDeviceIdentity(DeviceId, "homeassistant", "ChargeKeeper (Office-X1)"),
            new DiscoveryDevice("ZeroZero Software", "ChargeKeeper", "1.22.0"),
            new DiscoveryOrigin("ChargeKeeper", "1.22.0"),
            MqttTestBed.Declared().All,
            [],
            []);

        using var document = JsonDocument.Parse(json);
        return [.. document.RootElement.GetProperty("cmps").EnumerateObject()
                           .Select(entry => entry.Value.GetProperty("unique_id").GetString()!)];
    }

    [Fact]
    public void EveryUniqueId_IsTheOneAnInstallationAlreadyHas() =>
        // The guard the renaming rides on. A display name reaching a unique_id, or an entity id
        // edited alongside a name, discards the entity id, the area, the labels and every
        // automation attached to it, with no recovery.
        Assert.Equal(_uniqueIds, PublishedUniqueIds());

    /// <summary>One settings page, one leading word, and the entities that deliberately do not
    /// carry it. Named individually so the test states the policy rather than today's strings.</summary>
    private sealed record GroupWord
    {
        public required string Group { get; init; }

        public required string Word { get; init; }

        public IReadOnlyList<string> Exceptions { get; init; } = [];
    }

    private static readonly GroupWord[] _policy =
    [
        // The Sensors section holds nothing but Battery status, so a prefix earns no grouping there
        // and the two uncategorised readings keep their plain names. The adapter rating belongs to
        // the mains adapter and Energy Saver is an operating-system mode, so neither reads
        // correctly as a battery reading.
        new()
        {
            Group = MqttPublishGroups.BatteryStatus, Word = "Battery",
            Exceptions = ["Is charging", "On AC", "Adapter rating", "Low power mode"],
        },

        // Smart Charge keeps the product feature name the Settings page, the documentation and the
        // group key all use.
        new()
        {
            Group = MqttPublishGroups.SmartCharge, Word = "Charge",
            Exceptions = ["Smart Charge"],
        },

        new() { Group = MqttPublishGroups.KeepAwake, Word = "Keep" },

        // Smart Standby is a Control while the other seven are Configuration, so it can never sort
        // with them whatever it is called.
        new()
        {
            Group = MqttPublishGroups.LidClose, Word = "Lid-close",
            Exceptions = ["Smart Standby"],
        },

        new() { Group = MqttPublishGroups.Notifications, Word = "Notify" },
        new() { Group = MqttPublishGroups.Network, Word = "Network" },
        new() { Group = MqttPublishGroups.AppDiagnostics, Word = "App" },
    ];

    [Fact]
    public void EveryEntity_LeadsWithItsGroupWord()
    {
        var byGroup = MqttTestBed.Declared().All.ToLookup(e => e.Group);

        foreach (var policy in _policy)
        {
            var entities = byGroup[policy.Group].ToList();
            Assert.NotEmpty(entities);

            foreach (var entity in entities)
            {
                if (policy.Exceptions.Contains(entity.Name!, StringComparer.Ordinal))
                    continue;

                Assert.StartsWith(policy.Word, entity.Name!, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void EveryNamedException_IsStillAnEntityThatExists()
    {
        // An exception left behind after its entity was renamed would silently excuse a real
        // regression in the group it names.
        var byGroup = MqttTestBed.Declared().All.ToLookup(e => e.Group);

        foreach (var policy in _policy)
            foreach (string name in policy.Exceptions)
                Assert.Contains(byGroup[policy.Group], e => e.Name == name);
    }

    [Fact]
    public void EveryNamedException_ActuallyNeedsToBeOne() =>
        // An exception that already carries its word is dead weight and hides the group's real
        // state behind a permanent excuse.
        Assert.All(
            _policy.SelectMany(p => p.Exceptions.Select(name => (p.Word, Name: name))),
            pair => Assert.False(pair.Name.StartsWith(pair.Word, StringComparison.Ordinal)));

    [Fact]
    public void EveryGroup_IsCoveredByThePolicy() =>
        // A group added without a leading word would otherwise pass by never being looked at.
        Assert.Equal(
            MqttPublishGroups.Declared.Select(g => g.Key).Order(StringComparer.Ordinal),
            _policy.Select(p => p.Group).Order(StringComparer.Ordinal));

    [Fact]
    public void EachNotifySwitch_SortsImmediatelyAboveItsOwnThreshold()
    {
        // All six are Configuration, and the receiver sorts a section by display name. Nothing else
        // in that section leads with the word, so the six stand as one uninterrupted block.
        var configuration = MqttTestBed.Declared().All
            .Where(e => e.Category == MqttEntityCategory.Config)
            .Select(e => e.Name!)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var block = configuration
            .Where(name => name.StartsWith("Notify", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(
            [
                "Notify high battery", "Notify high battery level",
                "Notify low battery", "Notify low battery level",
                "Notify standby drain", "Notify standby drain rate",
            ],
            block);

        // Contiguous: the block as sorted is the same run of six the whole section holds.
        Assert.Equal(block, configuration.Skip(configuration.IndexOf(block[0])).Take(block.Count));
    }
}
