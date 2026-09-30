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
/// The identifiers a display-name change must never move.
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
        "chargekeeper_office_x1_system_temperature",
        "chargekeeper_office_x1_system_temperature_maximum",
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
        "chargekeeper_office_x1_screen_brightness",
        "chargekeeper_office_x1_screen_brightness_restore",
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
        "chargekeeper_office_x1_last_change",
        "chargekeeper_office_x1_last_change_time",
        "chargekeeper_office_x1_last_lid_event",
        "chargekeeper_office_x1_last_lid_event_time",
        "chargekeeper_office_x1_lid_wait",
        "chargekeeper_office_x1_lid_wait_remaining",
        "chargekeeper_office_x1_keep_awake_hold_remaining",
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
}
