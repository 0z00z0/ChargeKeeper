using System.Text.Json;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

// Pure-contract tests for the Home Assistant MQTT discovery layer (TODO #28/#29) — no broker.
public class HaDiscoveryTests
{
    private static List<(string Topic, string Json)> Configs(string node) =>
        HaDiscovery.DiscoveryConfigs(node, "homeassistant", "ChargeKeeper (PC)", "1.4.0").ToList();

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
    public void DiscoveryConfigs_CoverEveryEntity_AllShareDeviceAndAvailability()
    {
        string node = HaDiscovery.NodeId("PC");
        var configs = Configs(node);

        Assert.Equal(configs.Count, HaDiscovery.Entities.Count());
        // battery_level, battery_state, battery_power, battery_health, is_charging,
        // remaining_charge_time, on_ac, smart_charge, charge_start, charge_stop, adapter_watts.
        Assert.Equal(11, configs.Count);

        foreach (var (_, json) in configs)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.Equal(HaDiscovery.StateTopic(node), root.GetProperty("state_topic").GetString());
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
    public void DiscoveryConfigs_Issue29_RemainingChargeTime_IsDurationInMinutes()
    {
        string node = HaDiscovery.NodeId("PC");
        var (_, json) = Configs(node).Single(c => c.Topic == $"homeassistant/sensor/{node}/remaining_charge_time/config");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("duration", doc.RootElement.GetProperty("device_class").GetString());
        Assert.Equal("min", doc.RootElement.GetProperty("unit_of_measurement").GetString());
    }

    // ── State payload ────────────────────────────────────────────────────────────

    private static HaState State(
        int soc = 73, string batteryState = HaDiscovery.StateCharging, bool lowPower = false,
        int powerMw = 45000, bool isCharging = true, bool onAc = true, string? health = "Good",
        int? remaining = 40, bool smartCharge = true, int? start = 60, int? stop = 80, int? watts = 65)
        => new(soc, batteryState, lowPower, powerMw, isCharging, onAc, health, remaining,
               smartCharge, start, stop, watts);

    [Fact]
    public void StatePayload_AlwaysIncludesCoreFields()
    {
        var json = HaDiscovery.StatePayload(State(
            soc: 73, batteryState: HaDiscovery.StateNotCharging, isCharging: false, smartCharge: false,
            health: null, remaining: null, start: null, stop: null, watts: null));

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
    }
}
