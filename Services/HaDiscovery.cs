using System.Text;
using System.Text.Json;

namespace ChargeKeeper.Services;

/// <summary>A snapshot of the values published to Home Assistant. A nullable field is omitted from
/// the payload when unknown, so its entity reads "unknown" rather than a fabricated value.</summary>
internal readonly record struct HaState(
    int Soc,
    string BatteryState,
    bool LowPowerMode,
    int PowerMw,
    bool IsCharging,
    bool OnAc,
    string? Health,
    int? RemainingMinutes,
    bool SmartChargeEnabled,
    int? ChargeStart,
    int? ChargeStop,
    int? AdapterWatts,
    string? ActivePreset);

/// <summary>Pure builder for the Home Assistant MQTT-discovery contract: topic names, per-entity
/// discovery config JSON, and the shared state payload. No MQTT client, so it is testable without a
/// broker; <see cref="HomeAssistantService"/> owns the connection.</summary>
internal static class HaDiscovery
{
    // One retained discovery config per entity at <prefix>/<component>/<node>/<object>/config. All
    // entities share one JSON state topic and pull their own field via a value_template; one
    // availability topic drives online/offline; a shared device block groups them under one HA device.

    private const string BasePrefix = "chargekeeper";

    /// <summary>Longest node id accepted from the user — keeps the id readable inside a topic path.</summary>
    public const int MaxNodeIdLength = 48;

    /// <summary>Lower-cases and reduces to [a-z0-9_] (HA object-id/topic-safe), reporting whether
    /// anything alphanumeric survived.</summary>
    private static (string Text, bool HasAlnum) Sanitise(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        bool hasAlnum = false;
        foreach (char c in raw.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c)) { sb.Append(c); hasAlnum = true; }
            else sb.Append('_');
        }
        return (sb.ToString(), hasAlnum);
    }

    /// <summary>Stable per-machine node id, e.g. "chargekeeper_espen_x1". A machine name of only
    /// punctuation would sanitise to all underscores, so it falls back to "device".</summary>
    public static string NodeId(string machineName)
    {
        var (text, hasAlnum) = Sanitise(machineName);
        return $"{BasePrefix}_{(hasAlnum ? text : "device")}";
    }

    /// <summary>A user-typed node id reduced to the same alphabet <see cref="NodeId"/> produces, capped
    /// at <see cref="MaxNodeIdLength"/>. The <c>chargekeeper_</c> prefix is deliberately not forced —
    /// only the machine-name default carries it.</summary>
    public static string NormalizeNodeId(string raw)
    {
        var (text, _) = Sanitise(raw.Trim());
        return text.Length <= MaxNodeIdLength ? text : text[..MaxNodeIdLength];
    }

    /// <summary>Why a user-typed node id is unusable, or null when it is fine. Blank is not an error —
    /// an empty setting means "use the machine-name default".</summary>
    public static string? ValidateNodeId(string raw)
    {
        string trimmed = raw.Trim();
        if (trimmed.Length == 0) return null;
        if (trimmed.Length > MaxNodeIdLength) return $"An id can be at most {MaxNodeIdLength} characters.";
        if (!Sanitise(trimmed).HasAlnum) return "An id must contain at least one letter or digit.";
        return null;
    }

    /// <summary>The node id actually published under: the sanitised <paramref name="custom"/>, or the
    /// machine-name default. A custom value that sanitises to nothing usable — reachable by hand-editing
    /// settings.json past the validator — falls back too.</summary>
    public static string EffectiveNodeId(string? custom, string machineName)
    {
        if (string.IsNullOrWhiteSpace(custom)) return NodeId(machineName);
        string id = NormalizeNodeId(custom);
        return id.Any(char.IsAsciiLetterOrDigit) ? id : NodeId(machineName);
    }

    public static string StateTopic(string nodeId)        => $"{BasePrefix}/{nodeId}/state";
    public static string AvailabilityTopic(string nodeId) => $"{BasePrefix}/{nodeId}/availability";

    /// <summary>The command topic for one command entity, e.g. <c>chargekeeper/&lt;node&gt;/cmd/smart_charge</c>.</summary>
    public static string CommandTopic(string nodeId, string objectId) => $"{BasePrefix}/{nodeId}/cmd/{objectId}";

    public static string CommandTopicFilter(string nodeId) => $"{BasePrefix}/{nodeId}/cmd/#";

    /// <summary>The command object-id parsed out of a full command topic, or null if it isn't one.</summary>
    public static string? CommandObjectId(string nodeId, string topic)
    {
        string prefix = $"{BasePrefix}/{nodeId}/cmd/";
        return topic.StartsWith(prefix, StringComparison.Ordinal) && topic.Length > prefix.Length
            ? topic[prefix.Length..]
            : null;
    }

    public static string ConfigTopic(string prefix, string component, string nodeId, string objectId) =>
        $"{prefix}/{component}/{nodeId}/{objectId}/config";

    public const string Online  = "online";
    public const string Offline = "offline";

    /// <summary>Placeholder option for the preset <c>select</c>: HA rejects an empty options list.</summary>
    public const string NoPresetOption = "(none)";

    /// <summary>Resets the preset <c>select</c> to unknown. HA ignores an empty payload on a select, so
    /// omitting the field would leave it showing the last preset name forever.</summary>
    public const string PresetNone = "None";

    // Aligned with the HA mobile app's sensor.battery_state values.
    public const string StateCharging    = "Charging";
    public const string StateNotCharging = "Not Charging";
    public const string StateFull        = "Full";

    // Shared so the discovery config, the command router and the tests name each entity identically.
    public const string CmdSmartCharge  = "smart_charge";
    public const string CmdChargeStart  = "charge_start";
    public const string CmdChargeStop   = "charge_stop";
    public const string CmdChargeToFull = "charge_to_full";
    public const string CmdPreset       = "preset";

    // One definition per entity, so DiscoveryConfigs and the retained-clear list stay in sync.
    private sealed record Entity(string ObjectId, string Component, string Name, bool IsCommand, Dictionary<string, object> Extra);

    private static readonly Entity[] _entities =
    [
        new("battery_level", "sensor", "Battery level", false, new()
        {
            ["device_class"] = "battery", ["unit_of_measurement"] = "%", ["state_class"] = "measurement",
            ["value_template"] = "{{ value_json.battery_level }}",
        }),
        new("battery_state", "sensor", "Battery state", false, new()
        {
            ["icon"] = "mdi:battery-charging",
            ["value_template"] = "{{ value_json.battery_state }}",
            // Low Power Mode is an attribute on this sensor, matching the mobile app.
            ["json_attributes_topic"]    = "",  // filled with the state topic in DiscoveryConfigs
            ["json_attributes_template"] = "{{ {'low_power_mode': value_json.low_power_mode} | tojson }}",
        }),
        new("battery_power", "sensor", "Battery power", false, new()
        {
            ["device_class"] = "power", ["unit_of_measurement"] = "W", ["state_class"] = "measurement",
            // mW → W, one decimal; positive = charging/input, negative = draining.
            ["value_template"] = "{{ (value_json.power_mw | float / 1000) | round(1) }}",
        }),
        new("battery_health", "sensor", "Battery health", false, new()
        {
            ["icon"] = "mdi:heart-pulse",
            ["value_template"] = "{{ value_json.battery_health }}",
        }),
        new("is_charging", "binary_sensor", "Is charging", false, new()
        {
            ["device_class"] = "battery_charging",
            ["value_template"] = "{{ 'ON' if value_json.is_charging else 'OFF' }}",
            ["payload_on"] = "ON", ["payload_off"] = "OFF",
        }),
        new("remaining_charge_time", "sensor", "Remaining charge time", false, new()
        {
            ["device_class"] = "duration", ["unit_of_measurement"] = "min",
            ["icon"] = "mdi:timer-sand",
            ["value_template"] = "{{ value_json.remaining_min }}",
        }),
        new("on_ac", "binary_sensor", "On AC", false, new()
        {
            ["device_class"] = "plug",
            ["value_template"] = "{{ 'ON' if value_json.on_ac else 'OFF' }}",
            ["payload_on"] = "ON", ["payload_off"] = "OFF",
        }),
        new("adapter_watts", "sensor", "Adapter rating", false, new()
        {
            ["device_class"] = "power", ["unit_of_measurement"] = "W",
            ["value_template"] = "{{ value_json.adapter_watts }}",
        }),

        new(CmdSmartCharge, "switch", "Smart Charge", true, new()
        {
            ["icon"] = "mdi:battery-heart-variant",
            ["value_template"] = "{{ 'ON' if value_json.smart_charge else 'OFF' }}",
            ["payload_on"] = "ON", ["payload_off"] = "OFF",
            ["state_on"] = "ON", ["state_off"] = "OFF",
        }),
        new(CmdChargeStart, "number", "Charge threshold start", true, new()
        {
            ["unit_of_measurement"] = "%", ["icon"] = "mdi:battery-arrow-up",
            ["min"] = PresetEditValidator.MinThreshold, ["max"] = PresetEditValidator.MaxThreshold,
            ["step"] = 1, ["mode"] = "slider",
            ["value_template"] = "{{ value_json.charge_start }}",
        }),
        new(CmdChargeStop, "number", "Charge threshold end", true, new()
        {
            ["unit_of_measurement"] = "%", ["icon"] = "mdi:battery-arrow-down",
            ["min"] = PresetEditValidator.MinThreshold, ["max"] = PresetEditValidator.MaxThreshold,
            ["step"] = 1, ["mode"] = "slider",
            ["value_template"] = "{{ value_json.charge_stop }}",
        }),
        new(CmdChargeToFull, "button", "Charge to 100 % once", true, new()
        {
            ["icon"] = "mdi:battery-charging-100",
            ["payload_press"] = HaCommand.ButtonPress,
        }),
        new(CmdPreset, "select", "Charge preset", true, new()
        {
            ["icon"] = "mdi:playlist-check",
            ["value_template"] = "{{ value_json.active_preset }}",
            // "options" is injected per-call in DiscoveryConfigs from the configured preset names.
        }),
    ];

    /// <summary>The object ids of every entity, for clearing retained discovery configs on disable.</summary>
    public static IEnumerable<(string Component, string ObjectId)> Entities =>
        _entities.Select(e => (e.Component, e.ObjectId));

    /// <summary>Config topics an earlier entity set owned. The HA component is part of the config
    /// topic, so a component change or an object-id rename orphans the retained config at its old path
    /// and leaves an upgrading user a ghost entity; each gets an empty retained payload.</summary>
    public static readonly (string Component, string ObjectId)[] LegacyEntities =
    [
        ("sensor",        "soc"),          // → sensor/battery_level
        ("sensor",        "power"),        // → sensor/battery_power
        ("binary_sensor", "smart_charge"), // → switch/smart_charge
        ("sensor",        "charge_start"), // → number/charge_start
        ("sensor",        "charge_stop"),  // → number/charge_stop
    ];

    /// <summary>Every retained topic a node id owns: each current and legacy discovery config, the
    /// availability topic, and the state topic. An empty retained payload to each evicts the device
    /// from HA. State belongs here because it is published retained too — leave it out and a payload
    /// is stranded on the broker under the abandoned id.</summary>
    public static IEnumerable<string> TopicsToClear(string nodeId, string discoveryPrefix)
    {
        foreach (var (component, objectId) in Entities.Concat(LegacyEntities))
            yield return ConfigTopic(discoveryPrefix, component, nodeId, objectId);
        yield return AvailabilityTopic(nodeId);
        yield return StateTopic(nodeId);
    }

    private static Dictionary<string, object> Device(string nodeId, string deviceName, string swVersion) => new()
    {
        ["identifiers"]  = new[] { nodeId },
        ["name"]         = deviceName,
        ["manufacturer"] = "ZeroZero Software",
        ["model"]        = "ChargeKeeper",
        ["sw_version"]   = swVersion,
    };

    /// <summary>The retained discovery configs to publish on connect: one (topic, json) per entity.
    /// <paramref name="presetNames"/> populates the preset <c>select</c>'s options.</summary>
    public static IEnumerable<(string Topic, string Json)> DiscoveryConfigs(
        string nodeId, string discoveryPrefix, string deviceName, string swVersion, IReadOnlyList<string> presetNames)
    {
        string state = StateTopic(nodeId);
        string avail = AvailabilityTopic(nodeId);
        var device   = Device(nodeId, deviceName, swVersion);

        foreach (var e in _entities)
        {
            var config = new Dictionary<string, object>
            {
                ["name"]               = e.Name,
                ["unique_id"]          = $"{nodeId}_{e.ObjectId}",
                ["object_id"]          = $"{nodeId}_{e.ObjectId}",
                ["availability_topic"] = avail,
                ["payload_available"]     = Online,
                ["payload_not_available"] = Offline,
                ["device"]             = device,
            };
            // A button has no state; every other entity reflects the shared state topic.
            if (e.Component != "button")
                config["state_topic"] = state;
            if (e.IsCommand)
                config["command_topic"] = CommandTopic(nodeId, e.ObjectId);

            foreach (var (k, v) in e.Extra)
                // The attribute topic depends on the node id, so it can't be a const in the table.
                config[k] = (k == "json_attributes_topic") ? state : v;

            // HA rejects a select with no options, so an empty preset list gets a placeholder.
            if (e.ObjectId == CmdPreset)
                config["options"] = presetNames.Count > 0
                    ? presetNames
                    : (IReadOnlyList<string>)[NoPresetOption];

            yield return (ConfigTopic(discoveryPrefix, e.Component, nodeId, e.ObjectId),
                          JsonSerializer.Serialize(config));
        }
    }

    /// <summary>The shared state payload: the always-present battery fields, plus the optional ones
    /// only when known. <c>active_preset</c> is the exception — see <see cref="PresetNone"/>.</summary>
    public static string StatePayload(HaState s)
    {
        var payload = new Dictionary<string, object>
        {
            ["battery_level"]  = s.Soc,
            ["battery_state"]  = s.BatteryState,
            ["low_power_mode"] = s.LowPowerMode,
            ["power_mw"]       = s.PowerMw,
            ["is_charging"]    = s.IsCharging,
            ["on_ac"]          = s.OnAc,
            ["smart_charge"]   = s.SmartChargeEnabled,
        };
        if (s.Health is { } h)          payload["battery_health"] = h;
        if (s.RemainingMinutes is { } r) payload["remaining_min"]  = r;
        if (s.ChargeStart is { } cs)    payload["charge_start"]   = cs;
        if (s.ChargeStop  is { } ce)    payload["charge_stop"]    = ce;
        if (s.AdapterWatts is { } w)    payload["adapter_watts"]  = w;
        payload["active_preset"] = s.ActivePreset ?? PresetNone;   // never omitted — see PresetNone
        return JsonSerializer.Serialize(payload);
    }
}
