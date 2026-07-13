using System.Text.Json;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

// Pure-contract tests for the Home Assistant MQTT discovery layer (TODO #28) — no broker needed.
public class HaDiscoveryTests
{
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
        Assert.Equal($"homeassistant/sensor/{node}/soc/config",
                     HaDiscovery.ConfigTopic("homeassistant", "sensor", node, "soc"));
    }

    [Fact]
    public void DiscoveryConfigs_OneRetainedConfigPerEntity_AllShareDeviceAndAvailability()
    {
        string node = HaDiscovery.NodeId("PC");
        var configs = HaDiscovery.DiscoveryConfigs(node, "homeassistant", "ChargeKeeper (PC)", "1.2.20").ToList();

        Assert.Equal(6, configs.Count);
        // Object-id and component appear in the discovery topic path per HA's convention.
        Assert.Contains(configs, c => c.Topic == $"homeassistant/binary_sensor/{node}/on_ac/config");

        foreach (var (topic, json) in configs)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.Equal(HaDiscovery.StateTopic(node), root.GetProperty("state_topic").GetString());
            Assert.Equal(HaDiscovery.AvailabilityTopic(node), root.GetProperty("availability_topic").GetString());
            Assert.StartsWith($"{node}_", root.GetProperty("unique_id").GetString());
            // Every entity carries the same device identity so HA groups them.
            Assert.Equal(node, root.GetProperty("device").GetProperty("identifiers")[0].GetString());
            Assert.Equal("ZeroZero Software", root.GetProperty("device").GetProperty("manufacturer").GetString());
        }
    }

    [Fact]
    public void DiscoveryConfigs_BinarySensor_HasOnOffPayloads()
    {
        string node = HaDiscovery.NodeId("PC");
        var (_, json) = HaDiscovery.DiscoveryConfigs(node, "homeassistant", "ChargeKeeper", "1.0.0")
            .Single(c => c.Topic.Contains("/binary_sensor/"));

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("ON",  doc.RootElement.GetProperty("payload_on").GetString());
        Assert.Equal("OFF", doc.RootElement.GetProperty("payload_off").GetString());
        Assert.Equal("plug", doc.RootElement.GetProperty("device_class").GetString());
    }

    [Fact]
    public void StatePayload_AlwaysIncludesCoreFields()
    {
        var json = HaDiscovery.StatePayload(new HaState(Soc: 73, PowerMw: 45000, OnAc: true,
            ChargeStart: null, ChargeStop: null, AdapterWatts: null));

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(73, doc.RootElement.GetProperty("soc").GetInt32());
        Assert.Equal(45000, doc.RootElement.GetProperty("power_mw").GetInt32());
        Assert.True(doc.RootElement.GetProperty("on_ac").GetBoolean());
        // Unknown optionals are omitted so their HA entity reads "unknown", not a fake 0.
        Assert.False(doc.RootElement.TryGetProperty("charge_start", out _));
        Assert.False(doc.RootElement.TryGetProperty("adapter_watts", out _));
    }

    [Fact]
    public void StatePayload_IncludesOptionalFieldsWhenKnown()
    {
        var json = HaDiscovery.StatePayload(new HaState(Soc: 80, PowerMw: -18000, OnAc: false,
            ChargeStart: 60, ChargeStop: 80, AdapterWatts: 65));

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(60, doc.RootElement.GetProperty("charge_start").GetInt32());
        Assert.Equal(80, doc.RootElement.GetProperty("charge_stop").GetInt32());
        Assert.Equal(65, doc.RootElement.GetProperty("adapter_watts").GetInt32());
        Assert.Equal(-18000, doc.RootElement.GetProperty("power_mw").GetInt32());
    }
}
