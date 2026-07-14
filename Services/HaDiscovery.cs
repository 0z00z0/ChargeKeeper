using System.Text;
using System.Text.Json;

namespace ChargeKeeper.Services;

/// <summary>
/// A single snapshot of the values ChargeKeeper publishes to Home Assistant (TODO #28/#29/#30).
/// <para>
/// The read-only battery sensors mirror the Home Assistant mobile-app convention (issue #29):
/// <c>Soc</c> → sensor.battery_level, <c>BatteryState</c> → sensor.battery_state (Charging / Not
/// Charging / Full) with a <c>LowPowerMode</c> attribute, <c>PowerMw</c> → sensor.battery_power,
/// <c>IsCharging</c> → binary_sensor.is_charging, <c>Health</c> → sensor.battery_health, and
/// <c>RemainingMinutes</c> → sensor.remaining_charge_time (omitted while discharging so its entity
/// reads "unknown"). Battery temperature and cycle count are NOT published — Windows exposes no
/// reliable per-battery source for either (see the issue #29 comment).
/// </para>
/// <para>
/// The remaining fields drive the charge-control command entities (issue #30) and their reflected
/// state: <c>SmartChargeEnabled</c> (switch), <c>ChargeStart</c>/<c>ChargeStop</c> (number),
/// <c>ActivePreset</c> (select). <c>AdapterWatts</c> is null off AC / on hardware with no
/// adapter-wattage reading, so its entity reads "unknown" rather than a fabricated 0.
/// </para>
/// </summary>
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

/// <summary>
/// PURE builder for the Home Assistant MQTT-discovery contract (TODO #28/#29/#30) — topic names,
/// per-entity discovery config JSON, and the shared state payload. Kept free of any MQTT client so
/// the protocol contract (the fiddly part, modelled on HA's own mobile-app entity set) is
/// unit-testable without a live broker; <see cref="HomeAssistantService"/> owns the actual
/// connection and just publishes what this produces and routes inbound commands (see
/// <see cref="HaCommand"/>).
/// <para>
/// Layout: one retained discovery config per entity at
/// <c>&lt;prefix&gt;/&lt;component&gt;/&lt;node&gt;/&lt;object&gt;/config</c>; all entities share one
/// JSON state topic (<c>chargekeeper/&lt;node&gt;/state</c>) and pull their own field via a
/// <c>value_template</c>; a single availability topic drives online/offline (the LWT publishes
/// "offline"). Command entities (issue #30) additionally carry a <c>command_topic</c> under
/// <c>chargekeeper/&lt;node&gt;/cmd/&lt;object&gt;</c>. All entities carry the same <c>device</c>
/// block so HA groups them under one device.
/// </para>
/// </summary>
internal static class HaDiscovery
{
    private const string BasePrefix = "chargekeeper";

    /// <summary>
    /// Stable per-machine node id, e.g. "chargekeeper_espen_x1". Lower-cased and reduced to
    /// [a-z0-9_] (HA object-id/topic-safe); a machine name of only punctuation falls back to
    /// "device" so the id is never empty.
    /// </summary>
    public static string NodeId(string machineName)
    {
        var sb = new StringBuilder(machineName.Length);
        bool hasAlnum = false;
        foreach (char c in machineName.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c)) { sb.Append(c); hasAlnum = true; }
            else sb.Append('_');
        }
        // A name with no usable alphanumerics would be all underscores — use a readable fallback.
        return $"{BasePrefix}_{(hasAlnum ? sb.ToString() : "device")}";
    }

    public static string StateTopic(string nodeId)        => $"{BasePrefix}/{nodeId}/state";
    public static string AvailabilityTopic(string nodeId) => $"{BasePrefix}/{nodeId}/availability";

    /// <summary>The command topic for one command entity, e.g. <c>chargekeeper/&lt;node&gt;/cmd/smart_charge</c>.</summary>
    public static string CommandTopic(string nodeId, string objectId) => $"{BasePrefix}/{nodeId}/cmd/{objectId}";

    /// <summary>The single wildcard the service subscribes to for ALL inbound commands.</summary>
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

    // Battery-state strings, aligned with the HA mobile app's sensor.battery_state values.
    public const string StateCharging    = "Charging";
    public const string StateNotCharging = "Not Charging";
    public const string StateFull        = "Full";

    // Command entity object-ids (issue #30). Shared so the discovery config, the command router,
    // and the tests all name them identically.
    public const string CmdSmartCharge  = "smart_charge";
    public const string CmdChargeStart  = "charge_start";
    public const string CmdChargeStop   = "charge_stop";
    public const string CmdChargeToFull = "charge_to_full";
    public const string CmdPreset       = "preset";

    // Entity definitions: object id, HA component, friendly name, whether it accepts commands, and
    // the extra discovery fields (device_class/unit/value_template/payloads/etc.). Kept in one place
    // so DiscoveryConfigs and the retained-clear list stay in sync.
    private sealed record Entity(string ObjectId, string Component, string Name, bool IsCommand, Dictionary<string, object> Extra);

    private static readonly Entity[] _entities =
    [
        // ── Read-only battery sensors (issue #29) — HA mobile-app naming/semantics ──────────────
        new("battery_level", "sensor", "Battery level", false, new()
        {
            ["device_class"] = "battery", ["unit_of_measurement"] = "%", ["state_class"] = "measurement",
            ["value_template"] = "{{ value_json.battery_level }}",
        }),
        new("battery_state", "sensor", "Battery state", false, new()
        {
            ["icon"] = "mdi:battery-charging",
            ["value_template"] = "{{ value_json.battery_state }}",
            // Low Power Mode surfaces as an attribute on this sensor, matching the mobile app.
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
            // Omitted from state while discharging → the entity reads "unknown/unavailable".
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

        // ── Charge-control command entities (issue #30) — typed/enumerated, no free text ────────
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

    private static Dictionary<string, object> Device(string nodeId, string deviceName, string swVersion) => new()
    {
        ["identifiers"]  = new[] { nodeId },
        ["name"]         = deviceName,
        ["manufacturer"] = "ZeroZero Software",
        ["model"]        = "ChargeKeeper",
        ["sw_version"]   = swVersion,
    };

    /// <summary>
    /// The retained discovery configs to publish on connect: one (topic, json) per entity.
    /// <paramref name="presetNames"/> populates the preset <c>select</c>'s options (empty list → an
    /// empty picker, which is valid). Serialized with default options; value_templates contain
    /// literal <c>{{ }}</c> which are fine inside a JSON string.
    /// </summary>
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
                // The battery_state attribute topic is the shared state topic (couldn't be a const
                // above because it depends on the node id).
                config[k] = (k == "json_attributes_topic") ? state : v;

            // The preset select's options are dynamic (the configured preset names).
            if (e.ObjectId == CmdPreset)
                config["options"] = presetNames;

            yield return (ConfigTopic(discoveryPrefix, e.Component, nodeId, e.ObjectId),
                          JsonSerializer.Serialize(config));
        }
    }

    /// <summary>
    /// The shared state payload. Always-present battery fields plus optional fields only when known —
    /// an omitted field renders its entity "unknown" in HA. <c>remaining_min</c> is omitted while
    /// discharging (issue #29), <c>charge_start</c> is omitted with Smart Charge off, and
    /// <c>adapter_watts</c>/<c>battery_health</c>/<c>active_preset</c> are omitted when unknown.
    /// </summary>
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
        if (s.ActivePreset is { } p)    payload["active_preset"]  = p;
        return JsonSerializer.Serialize(payload);
    }
}
