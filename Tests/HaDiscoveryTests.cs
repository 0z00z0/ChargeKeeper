using System.Text.Json;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

// Pure-contract tests for the Home Assistant MQTT discovery layer. No broker.
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

    // Configurable node id

    [Theory]
    [InlineData("Office ThinkPad", "office_thinkpad")]
    [InlineData("  Padded  ", "padded")]                 // trimmed, so the padding isn't baked in
    [InlineData("Böx.2", "b_x_2")]                       // same alphabet NodeId reduces to
    [InlineData("ALREADY_ok_9", "already_ok_9")]
    public void NormalizeNodeId_MirrorsNodeIdSanitation_WithoutForcingThePrefix(string raw, string expected)
    {
        // The chargekeeper_ prefix belongs to the default only; a typed id is stored as typed.
        Assert.Equal(expected, HaDiscovery.NormalizeNodeId(raw));
    }

    [Fact]
    public void NormalizeNodeId_TruncatesToTheMaximum()
    {
        string id = HaDiscovery.NormalizeNodeId(new string('a', HaDiscovery.MaxNodeIdLength + 10));
        Assert.Equal(HaDiscovery.MaxNodeIdLength, id.Length);
    }

    [Theory]
    [InlineData("")]                     // blank = "use the machine-name default", not an error
    [InlineData("   ")]
    [InlineData("office_thinkpad")]
    [InlineData("Office ThinkPad")]      // sanitised on the way in, so it's accepted as typed
    public void ValidateNodeId_AcceptsBlankAndAnythingWithAnAlphanumeric(string raw)
    {
        Assert.Null(HaDiscovery.ValidateNodeId(raw));
    }

    [Theory]
    [InlineData("!!!")]                  // sanitises to all underscores — no usable id
    [InlineData("---")]
    public void ValidateNodeId_NoAlphanumeric_ReturnsError(string raw)
    {
        Assert.NotNull(HaDiscovery.ValidateNodeId(raw));
    }

    [Fact]
    public void ValidateNodeId_LongerThanTheMaximum_ReturnsError()
    {
        Assert.Null(HaDiscovery.ValidateNodeId(new string('a', HaDiscovery.MaxNodeIdLength)));
        Assert.NotNull(HaDiscovery.ValidateNodeId(new string('a', HaDiscovery.MaxNodeIdLength + 1)));
    }

    [Theory]
    [InlineData("", "ESPEN-X1", "chargekeeper_espen_x1")]      // unset → machine-name default
    [InlineData("   ", "ESPEN-X1", "chargekeeper_espen_x1")]
    [InlineData("!!!", "ESPEN-X1", "chargekeeper_espen_x1")]   // unusable custom → default, never "___"
    [InlineData("Office ThinkPad", "ESPEN-X1", "office_thinkpad")]
    [InlineData("kitchen_pc", "ESPEN-X1", "kitchen_pc")]
    public void EffectiveNodeId_PrefersACustomId_AndFallsBackToTheMachineName(
        string custom, string machine, string expected)
    {
        Assert.Equal(expected, HaDiscovery.EffectiveNodeId(custom, machine));
    }

    [Fact]
    public void TopicsToClear_CoversEveryRetainedTopicTheOldIdOwns()
    {
        var topics = HaDiscovery.TopicsToClear("old_id", "homeassistant").ToList();

        // Every current entity's discovery config…
        foreach (var (component, objectId) in HaDiscovery.Entities)
            Assert.Contains($"homeassistant/{component}/old_id/{objectId}/config", topics);
        // …every legacy one, so an id change leaves no renamed ghosts behind…
        foreach (var (component, objectId) in HaDiscovery.LegacyEntities)
            Assert.Contains($"homeassistant/{component}/old_id/{objectId}/config", topics);
        // …availability, and state. State is published retained and no other clear path covers it,
        // so an id change would strand a retained payload under the old id forever.
        Assert.Contains(HaDiscovery.AvailabilityTopic("old_id"), topics);
        Assert.Contains(HaDiscovery.StateTopic("old_id"), topics);

        Assert.Equal(HaDiscovery.Entities.Count() + HaDiscovery.LegacyEntities.Length + 2, topics.Count);
        Assert.Equal(topics.Count, topics.Distinct().Count());   // nothing cleared twice
    }

    [Fact]
    public void TopicsToClear_UsesTheGivenDiscoveryPrefix()
    {
        // The old id's configs live under the prefix they were published with, which may itself have
        // changed in the same settings apply.
        var topics = HaDiscovery.TopicsToClear("old_id", "ha").ToList();
        Assert.Contains("ha/sensor/old_id/battery_level/config", topics);
        Assert.DoesNotContain(topics, t => t.StartsWith("homeassistant/"));
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

    [Fact]
    public void DiscoveryConfigs_Issue30_PresetSelect_EmptyPresets_FallsBackToNonEmptyOptions()
    {
        // HA rejects a select with an empty options list, so with no presets configured a single
        // placeholder is published instead.
        string node = HaDiscovery.NodeId("PC");
        var (_, json) = HaDiscovery.DiscoveryConfigs(node, "homeassistant", "ChargeKeeper (PC)", "1.4.0", [])
            .Single(c => c.Topic == $"homeassistant/select/{node}/preset/config");

        using var doc = JsonDocument.Parse(json);
        var options = doc.RootElement.GetProperty("options").EnumerateArray().Select(x => x.GetString()).ToArray();
        Assert.NotEmpty(options);
        Assert.Equal(new[] { HaDiscovery.NoPresetOption }, options);
    }

    [Fact]
    public void LegacyEntities_CoverThePre29RenamedIds_ForRetainedClear()
    {
        // Renamed entities whose retained discovery must be evicted.
        var legacy = HaDiscovery.LegacyEntities;
        Assert.Contains(("sensor", "soc"), legacy);
        Assert.Contains(("sensor", "power"), legacy);
        Assert.Contains(("binary_sensor", "smart_charge"), legacy);
        Assert.Contains(("sensor", "charge_start"), legacy);
        Assert.Contains(("sensor", "charge_stop"), legacy);
        // An overlap between the current and legacy sets would clear a live entity.
        Assert.Empty(HaDiscovery.Entities.Intersect(legacy));
    }

    // State payload

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
    }

    [Fact]
    public void StatePayload_NoActivePreset_PublishesNone_RatherThanOmittingTheField()
    {
        // Unlike the other optionals, active_preset is not omitted when unknown: HA's MQTT select
        // ignores an empty payload and keeps the last option it saw, so it would go on claiming a
        // preset the device has moved off. "None" is HA's documented reset payload for a select.
        var json = HaDiscovery.StatePayload(State(preset: null));

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(HaDiscovery.PresetNone, doc.RootElement.GetProperty("active_preset").GetString());
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
