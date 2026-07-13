using System.Text;
using System.Text.Json;

namespace ChargeKeeper.Services;

/// <summary>
/// A single snapshot of the values ChargeKeeper publishes to Home Assistant (TODO #28).
/// <c>SmartChargeEnabled</c> reports whether Smart Charge is actively limiting the charge. When Smart
/// Charge is off, <c>ChargeStop</c> is 100 (charging is allowed all the way to full) and
/// <c>ChargeStart</c> is null so its HA entity reads "unknown/unavailable"; when on, both carry the
/// live thresholds. <c>AdapterWatts</c> is null off AC, and on a non-Lenovo laptop with no
/// adapter-wattage reading, so its entity reads "unknown" rather than a fabricated 0.
/// </summary>
internal readonly record struct HaState(
    int Soc,
    int PowerMw,
    bool OnAc,
    bool SmartChargeEnabled,
    int? ChargeStart,
    int? ChargeStop,
    int? AdapterWatts);

/// <summary>
/// PURE builder for the Home Assistant MQTT-discovery contract (TODO #28) — topic names, per-entity
/// discovery config JSON, and the shared state payload. Kept free of any MQTT client so the protocol
/// contract (the fiddly part, modelled on HASS.Agent's discovery layout) is unit-testable without a
/// live broker; <see cref="HomeAssistantService"/> owns the actual connection and just publishes what
/// this produces.
/// <para>
/// Layout: one retained discovery config per entity at
/// <c>&lt;prefix&gt;/&lt;component&gt;/&lt;node&gt;/&lt;object&gt;/config</c>; all entities share one
/// JSON state topic (<c>chargekeeper/&lt;node&gt;/state</c>) and pull their own field via a
/// <c>value_template</c>; a single availability topic drives online/offline (the LWT publishes
/// "offline"). All entities carry the same <c>device</c> block so HA groups them under one device.
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

    public static string ConfigTopic(string prefix, string component, string nodeId, string objectId) =>
        $"{prefix}/{component}/{nodeId}/{objectId}/config";

    public const string Online  = "online";
    public const string Offline = "offline";

    // Entity definitions: object id, HA component, friendly name, and the extra discovery fields
    // (device_class/unit/value_template/binary payloads). Kept in one place so DiscoveryConfigs and
    // any future entity list stay in sync.
    private sealed record Entity(string ObjectId, string Component, string Name, Dictionary<string, object> Extra);

    private static readonly Entity[] _entities =
    [
        new("soc", "sensor", "Battery", new()
        {
            ["device_class"] = "battery", ["unit_of_measurement"] = "%", ["state_class"] = "measurement",
            ["value_template"] = "{{ value_json.soc }}",
        }),
        new("power", "sensor", "Charge power", new()
        {
            ["device_class"] = "power", ["unit_of_measurement"] = "W", ["state_class"] = "measurement",
            // mW → W, one decimal; positive = charging, negative = draining.
            ["value_template"] = "{{ (value_json.power_mw | float / 1000) | round(1) }}",
        }),
        new("on_ac", "binary_sensor", "On AC", new()
        {
            ["device_class"] = "plug",
            ["value_template"] = "{{ 'ON' if value_json.on_ac else 'OFF' }}",
            ["payload_on"] = "ON", ["payload_off"] = "OFF",
        }),
        new("smart_charge", "binary_sensor", "Smart Charge", new()
        {
            ["value_template"] = "{{ 'ON' if value_json.smart_charge else 'OFF' }}",
            ["payload_on"] = "ON", ["payload_off"] = "OFF",
            ["icon"] = "mdi:battery-heart-variant",
        }),
        new("charge_start", "sensor", "Charge start", new()
        {
            ["unit_of_measurement"] = "%", ["icon"] = "mdi:battery-arrow-up",
            ["value_template"] = "{{ value_json.charge_start }}",
        }),
        new("charge_stop", "sensor", "Charge stop", new()
        {
            ["unit_of_measurement"] = "%", ["icon"] = "mdi:battery-arrow-down",
            ["value_template"] = "{{ value_json.charge_stop }}",
        }),
        new("adapter_watts", "sensor", "Adapter rating", new()
        {
            ["device_class"] = "power", ["unit_of_measurement"] = "W",
            ["value_template"] = "{{ value_json.adapter_watts }}",
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
    /// The retained discovery configs to publish on connect: one (topic, json) per entity. Serialized
    /// with default (unescaped-safe) options; value_templates contain literal <c>{{ }}</c> which are
    /// fine inside a JSON string.
    /// </summary>
    public static IEnumerable<(string Topic, string Json)> DiscoveryConfigs(
        string nodeId, string discoveryPrefix, string deviceName, string swVersion)
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
                ["state_topic"]        = state,
                ["availability_topic"] = avail,
                ["payload_available"]     = Online,
                ["payload_not_available"] = Offline,
                ["device"]             = device,
            };
            foreach (var (k, v) in e.Extra) config[k] = v;

            yield return (ConfigTopic(discoveryPrefix, e.Component, nodeId, e.ObjectId),
                          JsonSerializer.Serialize(config));
        }
    }

    /// <summary>
    /// The shared state payload. Always-present fields (soc/power/on_ac/smart_charge) plus optional
    /// fields only when known — an omitted field renders its entity "unknown" in HA. With Smart Charge
    /// off, <c>charge_stop</c> is still sent as 100 (charging allowed to full) while <c>charge_start</c>
    /// is omitted (reads "unknown"); <c>adapter_watts</c> is omitted with no adapter-wattage support.
    /// </summary>
    public static string StatePayload(HaState s)
    {
        var payload = new Dictionary<string, object>
        {
            ["soc"]          = s.Soc,
            ["power_mw"]     = s.PowerMw,
            ["on_ac"]        = s.OnAc,
            ["smart_charge"] = s.SmartChargeEnabled,
        };
        if (s.ChargeStart is { } cs) payload["charge_start"]  = cs;
        if (s.ChargeStop  is { } ce) payload["charge_stop"]   = ce;
        if (s.AdapterWatts is { } w) payload["adapter_watts"] = w;
        return JsonSerializer.Serialize(payload);
    }
}
