using System.Text.Json;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

// Pure-contract tests for the Home Assistant MQTT discovery layer (TODO #28/#29/#30) — no broker.
public class HaDiscoveryTests
{
    private static readonly string[] Presets = ["Daily", "Travel"];

    private static List<(string Topic, string Json)> Configs(string node) =>
        HaDiscovery.DiscoveryConfigs(node, "homeassistant", "ChargeKeeper (PC)", "1.4.0", Presets).ToList();

    [Theory]
    [InlineData("ESPEN-X1", "chargekeeper_espen_x1")]
    [InlineData("desk top pc", "chargekeeper_desk_top_pc")]
    [InlineData("Böx.2", "chargekeeper_b_x_2")]          // non-ascii + punctuation → underscores
    [InlineData("!!!", "chargekeeper_device")]           // all-punctuation → "device" fallback
    public void NodeId_SanitisesToTopicSafeLowercase(string machine, string expected)
    {
        Assert.Equal(expected, HaDiscovery.NodeId(machine));
    }

    [Fact]
    public void Topics_UseTheNodeIdAndPrefix()
    {
        string node = HaDiscovery.NodeId("PC");
        Assert.Equal($"chargekeeper/{node}/state", HaDiscovery.StateTopic(node));
        Assert.Equal($"chargekeeper/{node}/availability", HaDiscovery.AvailabilityTopic(node));
        Assert.Equal($"homeassistant/sensor/{node}/battery_level/config",
                     HaDiscovery.ConfigTopic("homeassistant", "sensor", node, "battery_level"));
    }

    [Fact]
    public void CommandTopics_RoundTripObjectId()
    {
        string node = HaDiscovery.NodeId("PC");
        Assert.Equal($"chargekeeper/{node}/cmd/smart_charge", HaDiscovery.CommandTopic(node, "smart_charge"));
        Assert.Equal($"chargekeeper/{node}/cmd/#", HaDiscovery.CommandTopicFilter(node));
        Assert.Equal("smart_charge", HaDiscovery.CommandObjectId(node, $"chargekeeper/{node}/cmd/smart_charge"));
        Assert.Null(HaDiscovery.CommandObjectId(node, $"chargekeeper/{node}/state"));  // not a command topic
        Assert.Null(HaDiscovery.CommandObjectId(node, $"chargekeeper/{node}/cmd/"));   // empty object id
    }

    [Fact]
    public void DiscoveryConfigs_CoverEveryEntity_AllShareDeviceAndAvailability()
    {
        string node = HaDiscovery.NodeId("PC");
        var configs = Configs(node);

        // 8 read-only sensors + 5 command entities.
        Assert.Equal(13, configs.Count);
        Assert.Equal(configs.Count, HaDiscovery.Entities.Count());

        foreach (var (_, json) in configs)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.Equal(HaDiscovery.AvailabilityTopic(node), root.GetProperty("availability_topic").GetString());
            Assert.StartsWith($"{node}_", root.GetProperty("unique_id").GetString());
            Assert.Equal(node, root.GetProperty("device").GetProperty("identifiers")[0].GetString());
            Assert.Equal("ZeroZero Software", root.GetProperty("device").GetProperty("manufacturer").GetString());
        }
    }

    [Fact]
    public void DiscoveryConfigs_Issue29_BatteryLevelSensor_HasBatteryDeviceClass()
    {
        string node = HaDiscovery.NodeId("PC");
        var (_, json) = Configs(node).Single(c => c.Topic == $"homeassistant/sensor/{node}/battery_level/config");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("battery", doc.RootElement.GetProperty("device_class").GetString());
        Assert.Equal("%", doc.RootElement.GetProperty("unit_of_measurement").GetString());
        Assert.Equal(HaDiscovery.StateTopic(node), doc.RootElement.GetProperty("state_topic").GetString());
    }

    [Fact]
    public void DiscoveryConfigs_Issue29_BatteryState_ExposesLowPowerModeAttribute()
    {
        string node = HaDiscovery.NodeId("PC");
        var (_, json) = Configs(node).Single(c => c.Topic == $"homeassistant/sensor/{node}/battery_state/config");

        using var doc = JsonDocument.Parse(json);
        // The attributes topic is the shared state topic (filled from the node id, not left blank).
        Assert.Equal(HaDiscovery.StateTopic(node), doc.RootElement.GetProperty("json_attributes_topic").GetString());
        Assert.Contains("low_power_mode", doc.RootElement.GetProperty("json_attributes_template").GetString());
    }

    [Fact]
    public void DiscoveryConfigs_Issue29_IsChargingBinarySensor_HasChargingDeviceClass()
    {
        string node = HaDiscovery.NodeId("PC");
        var (_, json) = Configs(node).Single(c => c.Topic == $"homeassistant/binary_sensor/{node}/is_charging/config");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("battery_charging", doc.RootElement.GetProperty("device_class").GetString());
        Assert.Equal("ON", doc.RootElement.GetProperty("payload_on").GetString());
    }

    [Fact]
    public void DiscoveryConfigs_Issue30_SmartChargeSwitch_HasCommandTopic()
    {
        string node = HaDiscovery.NodeId("PC");
        var (_, json) = Configs(node).Single(c => c.Topic == $"homeassistant/switch/{node}/smart_charge/config");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(HaDiscovery.CommandTopic(node, "smart_charge"),
                     doc.RootElement.GetProperty("command_topic").GetString());
        Assert.Equal("ON", doc.RootElement.GetProperty("payload_on").GetString());
        Assert.Equal("ON", doc.RootElement.GetProperty("state_on").GetString());
    }

    [Fact]
    public void DiscoveryConfigs_Issue30_ThresholdNumbers_AreBoundedNumberEntities()
    {
        string node = HaDiscovery.NodeId("PC");
        foreach (var obj in new[] { "charge_start", "charge_stop" })
        {
            var (_, json) = Configs(node).Single(c => c.Topic == $"homeassistant/number/{node}/{obj}/config");
            using var doc = JsonDocument.Parse(json);
            Assert.Equal(HaDiscovery.CommandTopic(node, obj), doc.RootElement.GetProperty("command_topic").GetString());
            Assert.Equal(PresetEditValidator.MinThreshold, doc.RootElement.GetProperty("min").GetInt32());
            Assert.Equal(PresetEditValidator.MaxThreshold, doc.RootElement.GetProperty("max").GetInt32());
        }
    }

    [Fact]
    public void DiscoveryConfigs_Issue30_ChargeToFullButton_HasNoStateTopic()
    {
        string node = HaDiscovery.NodeId("PC");
        var (_, json) = Configs(node).Single(c => c.Topic == $"homeassistant/button/{node}/charge_to_full/config");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(HaDiscovery.CommandTopic(node, "charge_to_full"),
                     doc.RootElement.GetProperty("command_topic").GetString());
        Assert.Equal(HaCommand.ButtonPress, doc.RootElement.GetProperty("payload_press").GetString());
        Assert.False(doc.RootElement.TryGetProperty("state_topic", out _));  // a button has no state
    }

    [Fact]
    public void DiscoveryConfigs_Issue30_PresetSelect_CarriesConfiguredOptions()
    {
        string node = HaDiscovery.NodeId("PC");
        var (_, json) = Configs(node).Single(c => c.Topic == $"homeassistant/select/{node}/preset/config");

        using var doc = JsonDocument.Parse(json);
        var options = doc.RootElement.GetProperty("options").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.Equal(Presets, options);
        Assert.Equal(HaDiscovery.CommandTopic(node, "preset"), doc.RootElement.GetProperty("command_topic").GetString());
    }

    // ── State payload ────────────────────────────────────────────────────────────

    private static HaState State(
        int soc = 73, string batteryState = HaDiscovery.StateCharging, bool lowPower = false,
        int powerMw = 45000, bool isCharging = true, bool onAc = true, string? health = "Good",
        int? remaining = 40, bool smartCharge = true, int? start = 60, int? stop = 80,
        int? watts = 65, string? preset = "Daily")
        => new(soc, batteryState, lowPower, powerMw, isCharging, onAc, health, remaining,
               smartCharge, start, stop, watts, preset);

    [Fact]
    public void StatePayload_AlwaysIncludesCoreFields()
    {
        var json = HaDiscovery.StatePayload(State(
            soc: 73, batteryState: HaDiscovery.StateNotCharging, isCharging: false, smartCharge: false,
            health: null, remaining: null, start: null, stop: null, watts: null, preset: null));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(73, root.GetProperty("battery_level").GetInt32());
        Assert.Equal(HaDiscovery.StateNotCharging, root.GetProperty("battery_state").GetString());
        Assert.False(root.GetProperty("is_charging").GetBoolean());
        Assert.False(root.GetProperty("smart_charge").GetBoolean());
        Assert.True(root.TryGetProperty("low_power_mode", out _));
        // Unknown optionals are omitted so their HA entity reads "unknown", not a fake value.
        Assert.False(root.TryGetProperty("battery_health", out _));
        Assert.False(root.TryGetProperty("remaining_min", out _));
        Assert.False(root.TryGetProperty("charge_start", out _));
        Assert.False(root.TryGetProperty("adapter_watts", out _));
        Assert.False(root.TryGetProperty("active_preset", out _));
    }

    [Fact]
    public void StatePayload_IncludesOptionalFieldsWhenKnown()
    {
        var json = HaDiscovery.StatePayload(State());

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("Good", root.GetProperty("battery_health").GetString());
        Assert.Equal(40, root.GetProperty("remaining_min").GetInt32());
        Assert.Equal(60, root.GetProperty("charge_start").GetInt32());
        Assert.Equal(80, root.GetProperty("charge_stop").GetInt32());
        Assert.Equal(65, root.GetProperty("adapter_watts").GetInt32());
        Assert.Equal("Daily", root.GetProperty("active_preset").GetString());
    }

    [Fact]
    public void Entities_IncludeCommandEntities_ForRetainedClearOnDisable()
    {
        var entities = HaDiscovery.Entities.ToList();
        Assert.Contains(("switch", HaDiscovery.CmdSmartCharge), entities);
        Assert.Contains(("number", HaDiscovery.CmdChargeStart), entities);
        Assert.Contains(("button", HaDiscovery.CmdChargeToFull), entities);
        Assert.Contains(("select", HaDiscovery.CmdPreset), entities);
    }
}
