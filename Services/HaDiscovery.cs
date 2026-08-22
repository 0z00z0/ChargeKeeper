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
    string? ActivePreset,
    int? FullMwh = null,
    int? DesignMwh = null);

/// <summary>Pure builder for the Home Assistant MQTT-discovery contract: topic names, per-entity
/// discovery config JSON, and the shared state payload. No MQTT client, so it is testable without a
/// broker; <see cref="HomeAssistantService"/> owns the connection.</summary>
internal static class HaDiscovery
{
    // One retained discovery config per entity at <prefix>/<component>/<node>/<object>/config. Entities
    // pull their own field from a shared JSON payload via a value_template; one availability topic
    // drives online/offline; a shared device block groups them under one HA device.
    //
    // There are two payload topics, not one. The battery values move on every reading and the settings
    // values only when something is changed, so a single payload would either re-send every setting on
    // each battery tick or leave a settings change waiting for one.

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

    /// <summary>The settings/network/diagnostic payload's own topic. Separate from the battery state
    /// so a settings change publishes at once and a battery tick does not re-send the settings.</summary>
    public static string StatusTopic(string nodeId)       => $"{BasePrefix}/{nodeId}/status";
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

    // The charge-control object ids, kept here because the command parser and the tests have always
    // named them from this class. The table itself lives in HaEntityCatalog.
    public const string CmdSmartCharge  = HaEntityCatalog.SmartCharge;
    public const string CmdChargeStart  = HaEntityCatalog.ChargeStart;
    public const string CmdChargeStop   = HaEntityCatalog.ChargeStop;
    public const string CmdChargeToFull = HaEntityCatalog.ChargeToFull;
    public const string CmdPreset       = HaEntityCatalog.Preset;

    /// <summary>The object ids of every entity the app can publish, for clearing retained discovery
    /// configs on disable. The full set, not the announced one: a node being abandoned must shed
    /// everything it ever owned, whatever it happens to be announcing now.</summary>
    public static IEnumerable<(string Component, string ObjectId)> Entities =>
        HaEntityCatalog.All.Select(e => (e.Component, e.ObjectId));

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
    /// availability topic, and both state topics. An empty retained payload to each evicts the device
    /// from HA. The state topics belong here because they are published retained too — leave them out
    /// and a payload is stranded on the broker under the abandoned id.</summary>
    public static IEnumerable<string> TopicsToClear(string nodeId, string discoveryPrefix)
    {
        foreach (var (component, objectId) in Entities.Concat(LegacyEntities))
            yield return ConfigTopic(discoveryPrefix, component, nodeId, objectId);
        yield return AvailabilityTopic(nodeId);
        yield return StateTopic(nodeId);
        yield return StatusTopic(nodeId);
    }

    private static Dictionary<string, object> Device(string nodeId, string deviceName, string swVersion) => new()
    {
        ["identifiers"]  = new[] { nodeId },
        ["name"]         = deviceName,
        ["manufacturer"] = "ZeroZero Software",
        ["model"]        = "ChargeKeeper",
        ["sw_version"]   = swVersion,
    };

    /// <summary>The retained discovery configs to publish on connect: one (topic, json) per announced
    /// entity. <paramref name="entities"/> comes from <see cref="HaEntityCatalog.Announce"/>, so what
    /// is published follows the group toggles and the vendor capabilities and nothing else.
    /// <paramref name="presetNames"/> populates the two preset-backed <c>select</c>s' options.</summary>
    public static IEnumerable<(string Topic, string Json)> DiscoveryConfigs(
        string nodeId, string discoveryPrefix, string deviceName, string swVersion,
        IReadOnlyList<string> presetNames, IReadOnlyList<HaEntity> entities)
    {
        string state  = StateTopic(nodeId);
        string status = StatusTopic(nodeId);
        string avail  = AvailabilityTopic(nodeId);
        var device    = Device(nodeId, deviceName, swVersion);

        foreach (var e in entities)
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
            // Uncategorised is a deliberate value, not a gap: it is what keeps a primary control on
            // the main card instead of behind the consumer's Configuration or Diagnostic fold.
            if (EntityCategory(e.Role) is { } category)
                config["entity_category"] = category;
            // A button has no state; everything else reads whichever payload carries its value.
            if (StateTopicFor(e, state, status) is { } stateTopic)
                config["state_topic"] = stateTopic;
            if (e.IsCommand)
                config["command_topic"] = CommandTopic(nodeId, e.ObjectId);

            foreach (var (k, v) in e.Extra)
                // The attribute topic depends on the node id, so it can't be a const in the table.
                config[k] = (k == "json_attributes_topic") ? state : v;

            // HA rejects a select with no options, so an empty preset list gets a placeholder.
            if (HaEntityCatalog.PresetOptionSelects.Contains(e.ObjectId))
                config["options"] = PresetOptions(e.ObjectId, presetNames);

            yield return (ConfigTopic(discoveryPrefix, e.Component, nodeId, e.ObjectId),
                          JsonSerializer.Serialize(config));
        }
    }

    /// <summary>The unknown-network picker offers "do nothing" alongside the presets; the charge
    /// preset picker does not, because there is no such thing as applying no preset.</summary>
    private static IReadOnlyList<string> PresetOptions(string objectId, IReadOnlyList<string> presetNames)
    {
        if (objectId == HaEntityCatalog.UnknownNetworkPreset)
            return [PresetEditValidator.UnknownNetworkSentinel, .. presetNames];
        return presetNames.Count > 0 ? presetNames : [NoPresetOption];
    }

    private static string? EntityCategory(HaEntityRole role) => role switch
    {
        HaEntityRole.Config     => "config",
        HaEntityRole.Diagnostic => "diagnostic",
        _                       => null,
    };

    private static string? StateTopicFor(HaEntity e, string state, string status) => e.State switch
    {
        HaStateSource.Live    => state,
        HaStateSource.Surface => status,
        _                     => null,
    };

    /// <summary>The discovery topics of the entities this configuration does NOT announce. An empty
    /// retained payload to each is how the discovery convention deletes an entity: without it a
    /// switched-off group would linger in the consumer as "unavailable" for ever, because the
    /// retained config that created it is still on the broker.</summary>
    public static IEnumerable<string> RemovalTopics(
        string nodeId, string discoveryPrefix, IReadOnlyList<HaEntity> withheld) =>
        withheld.Select(e => ConfigTopic(discoveryPrefix, e.Component, nodeId, e.ObjectId));

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
        if (s.FullMwh   is > 0 and { } fm) payload["capacity_full_mwh"]   = fm;
        if (s.DesignMwh is > 0 and { } dm) payload["capacity_design_mwh"] = dm;
        payload["active_preset"] = s.ActivePreset ?? PresetNone;   // never omitted — see PresetNone
        return JsonSerializer.Serialize(payload);
    }
}
