namespace ChargeKeeper.Services;

/// <summary>The publishing groups the MQTT page toggles, mirroring the Settings pages. Smart Standby
/// rides with <see cref="LidClose"/> rather than a group of its own: the dashboard already pairs them
/// — one decides how the machine sleeps, the other when.</summary>
internal enum HaCategory
{
    BatteryStatus,
    SmartCharge,
    KeepAwake,
    LidClose,
    Notifications,
    Network,
    AppDiagnostics,
}

/// <summary>How an entity is filed in the consumer's UI. <see cref="Primary"/> carries no
/// <c>entity_category</c>, so it stays on the main card.</summary>
internal enum HaEntityRole { Primary, Config, Diagnostic }

/// <summary>Which retained topic an entity reads. <see cref="None"/> is a button, which has no state;
/// <see cref="Live"/> is the battery/charge payload, <see cref="Surface"/> the settings payload.</summary>
internal enum HaStateSource { None, Live, Surface }

/// <summary>Which groups are switched on. A plain record struct rather than a settings reference, so
/// <see cref="HaEntityCatalog.Announce"/> can stay pure.</summary>
internal readonly record struct HaCategorySet(
    bool BatteryStatus, bool SmartCharge, bool KeepAwake,
    bool LidClose, bool Notifications, bool Network, bool AppDiagnostics)
{
    /// <summary>Every group on — the shipped default, and the baseline the tests compare against.</summary>
    public static readonly HaCategorySet All = new(true, true, true, true, true, true, true);

    public bool Includes(HaCategory category) => category switch
    {
        HaCategory.BatteryStatus  => BatteryStatus,
        HaCategory.SmartCharge    => SmartCharge,
        HaCategory.KeepAwake      => KeepAwake,
        HaCategory.LidClose       => LidClose,
        HaCategory.Notifications  => Notifications,
        HaCategory.Network        => Network,
        HaCategory.AppDiagnostics => AppDiagnostics,
        _                         => false,
    };
}

/// <summary>What the hardware can actually do, from the vendor gates the UI already uses. Announcing
/// a control the machine cannot honour would leave the consumer with an entity that silently does
/// nothing.</summary>
internal readonly record struct HaCapabilities(
    SmartChargeSurface SmartCharge, bool LidClose, bool SmartStandby)
{
    /// <summary>A machine with every gate open — the baseline the tests compare against.</summary>
    public static readonly HaCapabilities Full = new(SmartChargeSurface.Numeric, true, true);
}

/// <summary>One published entity: its topic segment, its consumer component, and the discovery keys
/// specific to it. <see cref="Extra"/> is merged into the config last, so an entry there wins.</summary>
internal sealed record HaEntity(
    string ObjectId, string Component, string Name,
    HaCategory Category, HaEntityRole Role, HaStateSource State, bool IsCommand,
    Dictionary<string, object> Extra);

/// <summary>
/// The full entity surface, and the pure decision of which part of it a given configuration
/// announces. Nothing here touches an MQTT client or the settings singleton: the group toggles and
/// the vendor capabilities go in, the entity set comes out, so the same call answers both "what to
/// publish" and "what to evict".
/// </summary>
internal static class HaEntityCatalog
{
    // Object ids. Shared so the discovery config, the command parser, the dispatcher and the tests
    // name each entity identically; a rename here is a rename everywhere.

    public const string BatteryLevel        = "battery_level";
    public const string BatteryState        = "battery_state";
    public const string BatteryPower        = "battery_power";
    public const string BatteryHealth       = "battery_health";
    public const string IsCharging          = "is_charging";
    public const string OnAc                = "on_ac";
    public const string RemainingChargeTime = "remaining_charge_time";
    public const string AdapterWatts        = "adapter_watts";
    public const string CapacityFull        = "capacity_full";
    public const string CapacityDesign      = "capacity_design";

    public const string SmartCharge     = "smart_charge";
    public const string ChargeStart     = "charge_start";
    public const string ChargeStop      = "charge_stop";
    public const string ChargeToFull    = "charge_to_full";
    public const string Preset          = "preset";
    public const string TravelOverride  = "travel_override";

    public const string KeepAwake          = "keep_awake";
    public const string KeepAwakeFor       = "keep_awake_for";
    public const string KeepAwakeExpires   = "keep_awake_expires";
    public const string KeepAwakeDisplayOn = "keep_awake_display_on";

    public const string LidDelay        = "lid_delay";
    public const string LidDelayMinutes = "lid_delay_minutes";
    public const string LidDelayLock    = "lid_delay_lock";
    public const string SmartStandby    = "smart_standby";

    public const string LowBatteryWarning  = "low_battery_warning";
    public const string LowBatteryLevel    = "low_battery_level";
    public const string HighBatteryWarning = "high_battery_warning";
    public const string HighBatteryLevel   = "high_battery_level";
    public const string DrainWarning       = "drain_warning";
    public const string DrainRate          = "drain_rate";

    public const string NetworkProfiles       = "network_profiles";
    public const string UnknownNetworkPreset  = "unknown_network_preset";
    public const string NetworkAdapterAlias   = "network_adapter_alias";
    public const string NetworkIpAddress      = "network_ip_address";
    public const string NetworkAdapterName    = "network_adapter_name";
    public const string NetworkProfileMatched = "network_profile";

    public const string AppVersion   = "app_version";
    public const string StartupDelay = "startup_delay";
    public const string IconMode     = "icon_mode";
    public const string DowntimeGap  = "downtime_gap";

    /// <summary>The two select entities whose options are the configured preset names, injected at
    /// publish time rather than baked into the table.</summary>
    public static readonly string[] PresetOptionSelects = [Preset, UnknownNetworkPreset];

    private static Dictionary<string, object> Live(string field, params (string Key, object Value)[] extra)
        => Merge(new() { ["value_template"] = $"{{{{ value_json.{field} }}}}" }, extra);

    private static Dictionary<string, object> Flag(string field, params (string Key, object Value)[] extra)
        => Merge(new()
        {
            ["value_template"] = $"{{{{ 'ON' if value_json.{field} else 'OFF' }}}}",
            ["payload_on"] = "ON", ["payload_off"] = "OFF",
        }, extra);

    private static Dictionary<string, object> Switch(string field, params (string Key, object Value)[] extra)
        => Merge(Flag(field), [.. extra, ("state_on", (object)"ON"), ("state_off", (object)"OFF")]);

    private static Dictionary<string, object> Number(
        string field, int min, int max, string unit, string icon, string mode = "box")
        => new()
        {
            ["value_template"] = $"{{{{ value_json.{field} }}}}",
            ["unit_of_measurement"] = unit, ["icon"] = icon,
            ["min"] = min, ["max"] = max, ["step"] = 1, ["mode"] = mode,
        };

    private static Dictionary<string, object> Merge(
        Dictionary<string, object> baseKeys, (string Key, object Value)[] extra)
    {
        foreach (var (k, v) in extra) baseKeys[k] = v;
        return baseKeys;
    }

    /// <summary>The tray icon styles, spelled as the enum so a round trip needs no lookup table.</summary>
    public static readonly string[] IconModeOptions =
        [nameof(TrayIconMode.Arc), nameof(TrayIconMode.Numeric), nameof(TrayIconMode.BrandMark)];

    // Deliberately absent, because the value means nothing outside this process: the Settings window's
    // saved placement, the last-selected graph scale, the travel override's revert pair (the override
    // itself is published), the lid-action values captured for crash recovery, and the once-only
    // network-rule migration flag. The broker block is absent for a different reason — it describes the
    // transport rather than the machine, and its credentials are a secret (see HaSurfaceState).
    // The saved lists — presets, keep-awake presets, network rules — reach the surface as the two
    // selects' options and the matched-profile sensor rather than as entities of their own.
    private static readonly HaEntity[] _all =
    [
        // Battery status. The five a dashboard would show stay uncategorised; the derived readings and
        // the raw capacities are diagnostics — health is the answer, the capacities are its workings.
        new(BatteryLevel, "sensor", "Battery level", HaCategory.BatteryStatus, HaEntityRole.Primary,
            HaStateSource.Live, false, Live("battery_level",
                ("device_class", "battery"), ("unit_of_measurement", "%"), ("state_class", "measurement"))),
        new(BatteryState, "sensor", "Battery state", HaCategory.BatteryStatus, HaEntityRole.Primary,
            HaStateSource.Live, false, Live("battery_state",
                ("icon", "mdi:battery-charging"),
                // Low Power Mode is an attribute here, matching the Home Assistant mobile app.
                ("json_attributes_topic", ""),   // filled with the state topic in DiscoveryConfigs
                ("json_attributes_template", "{{ {'low_power_mode': value_json.low_power_mode} | tojson }}"))),
        new(BatteryPower, "sensor", "Battery power", HaCategory.BatteryStatus, HaEntityRole.Primary,
            HaStateSource.Live, false, new()
            {
                ["device_class"] = "power", ["unit_of_measurement"] = "W", ["state_class"] = "measurement",
                // mW → W, one decimal; positive = charging/input, negative = draining.
                ["value_template"] = "{{ (value_json.power_mw | float / 1000) | round(1) }}",
            }),
        new(IsCharging, "binary_sensor", "Is charging", HaCategory.BatteryStatus, HaEntityRole.Primary,
            HaStateSource.Live, false, Flag("is_charging", ("device_class", "battery_charging"))),
        new(OnAc, "binary_sensor", "On AC", HaCategory.BatteryStatus, HaEntityRole.Primary,
            HaStateSource.Live, false, Flag("on_ac", ("device_class", "plug"))),
        new(BatteryHealth, "sensor", "Battery health", HaCategory.BatteryStatus, HaEntityRole.Diagnostic,
            HaStateSource.Live, false, Live("battery_health", ("icon", "mdi:heart-pulse"))),
        new(RemainingChargeTime, "sensor", "Remaining charge time", HaCategory.BatteryStatus,
            HaEntityRole.Diagnostic, HaStateSource.Live, false, Live("remaining_min",
                ("device_class", "duration"), ("unit_of_measurement", "min"), ("icon", "mdi:timer-sand"))),
        new(AdapterWatts, "sensor", "Adapter rating", HaCategory.BatteryStatus, HaEntityRole.Diagnostic,
            HaStateSource.Live, false, Live("adapter_watts",
                ("device_class", "power"), ("unit_of_measurement", "W"))),
        new(CapacityFull, "sensor", "Full-charge capacity", HaCategory.BatteryStatus, HaEntityRole.Diagnostic,
            HaStateSource.Live, false, new()
            {
                ["device_class"] = "energy_storage", ["unit_of_measurement"] = "Wh",
                ["value_template"] = "{{ (value_json.capacity_full_mwh | float / 1000) | round(1) }}",
            }),
        new(CapacityDesign, "sensor", "Design capacity", HaCategory.BatteryStatus, HaEntityRole.Diagnostic,
            HaStateSource.Live, false, new()
            {
                ["device_class"] = "energy_storage", ["unit_of_measurement"] = "Wh",
                ["value_template"] = "{{ (value_json.capacity_design_mwh | float / 1000) | round(1) }}",
            }),

        // Smart Charge.
        new(SmartCharge, "switch", "Smart Charge", HaCategory.SmartCharge, HaEntityRole.Primary,
            HaStateSource.Live, true, Switch("smart_charge", ("icon", "mdi:battery-heart-variant"))),
        new(ChargeStart, "number", "Charge threshold start", HaCategory.SmartCharge, HaEntityRole.Config,
            HaStateSource.Live, true, Number("charge_start", PresetEditValidator.MinThreshold,
                PresetEditValidator.MaxThreshold, "%", "mdi:battery-arrow-up", mode: "slider")),
        new(ChargeStop, "number", "Charge threshold end", HaCategory.SmartCharge, HaEntityRole.Config,
            HaStateSource.Live, true, Number("charge_stop", PresetEditValidator.MinThreshold,
                PresetEditValidator.MaxThreshold, "%", "mdi:battery-arrow-down", mode: "slider")),
        new(ChargeToFull, "button", "Charge to 100 % once", HaCategory.SmartCharge, HaEntityRole.Primary,
            HaStateSource.None, true, new()
            {
                ["icon"] = "mdi:battery-charging-100", ["payload_press"] = HaCommand.ButtonPress,
            }),
        new(Preset, "select", "Charge preset", HaCategory.SmartCharge, HaEntityRole.Primary,
            HaStateSource.Live, true, Live("active_preset", ("icon", "mdi:playlist-check"))),
        new(TravelOverride, "binary_sensor", "Charging to full once", HaCategory.SmartCharge,
            HaEntityRole.Diagnostic, HaStateSource.Surface, false,
            Flag("travel_override", ("icon", "mdi:airplane"))),

        // Keep Awake. The expiry is published as an instant rather than a countdown, so a running
        // session does not re-publish the whole payload once a minute.
        new(KeepAwake, "switch", "Keep awake", HaCategory.KeepAwake, HaEntityRole.Primary,
            HaStateSource.Surface, true, Switch("keep_awake", ("icon", "mdi:coffee"))),
        new(KeepAwakeFor, "text", "Keep awake for", HaCategory.KeepAwake, HaEntityRole.Config,
            HaStateSource.Surface, true, Live("keep_awake_for",
                ("icon", "mdi:timer-cog-outline"), ("max", 16))),
        new(KeepAwakeExpires, "sensor", "Keep awake until", HaCategory.KeepAwake, HaEntityRole.Diagnostic,
            HaStateSource.Surface, false, Live("keep_awake_expires", ("device_class", "timestamp"))),
        new(KeepAwakeDisplayOn, "switch", "Keep the display on", HaCategory.KeepAwake, HaEntityRole.Config,
            HaStateSource.Surface, true, Switch("keep_awake_display_on", ("icon", "mdi:monitor-shimmer"))),

        // Lid close, and the standby scheduling the dashboard pairs with it.
        new(LidDelay, "switch", "Lid-close delay", HaCategory.LidClose, HaEntityRole.Config,
            HaStateSource.Surface, true, Switch("lid_delay", ("icon", "mdi:laptop"))),
        new(LidDelayMinutes, "number", "Lid-close delay length", HaCategory.LidClose, HaEntityRole.Config,
            HaStateSource.Surface, true, Number("lid_delay_minutes", LidDelayPolicy.MinMinutes,
                LidDelayPolicy.MaxMinutes, "min", "mdi:timer-outline")),
        new(LidDelayLock, "switch", "Lock on lid close", HaCategory.LidClose, HaEntityRole.Config,
            HaStateSource.Surface, true, Switch("lid_delay_lock", ("icon", "mdi:lock"))),
        new(SmartStandby, "switch", "Smart Standby", HaCategory.LidClose, HaEntityRole.Primary,
            HaStateSource.Surface, true, Switch("smart_standby", ("icon", "mdi:sleep"))),

        // Notifications.
        new(LowBatteryWarning, "switch", "Low battery warning", HaCategory.Notifications, HaEntityRole.Config,
            HaStateSource.Surface, true, Switch("low_battery_warning", ("icon", "mdi:battery-alert"))),
        new(LowBatteryLevel, "number", "Low battery level", HaCategory.Notifications, HaEntityRole.Config,
            HaStateSource.Surface, true, Number("low_battery_level", SettingRanges.LowBatteryMin,
                SettingRanges.LowBatteryMax, "%", "mdi:battery-low")),
        new(HighBatteryWarning, "switch", "High battery warning", HaCategory.Notifications, HaEntityRole.Config,
            HaStateSource.Surface, true, Switch("high_battery_warning", ("icon", "mdi:battery-alert-variant"))),
        new(HighBatteryLevel, "number", "High battery level", HaCategory.Notifications, HaEntityRole.Config,
            HaStateSource.Surface, true, Number("high_battery_level", SettingRanges.HighBatteryMin,
                SettingRanges.HighBatteryMax, "%", "mdi:battery-high")),
        new(DrainWarning, "switch", "Standby drain warning", HaCategory.Notifications, HaEntityRole.Config,
            HaStateSource.Surface, true, Switch("drain_warning", ("icon", "mdi:battery-clock"))),
        new(DrainRate, "number", "Standby drain rate", HaCategory.Notifications, HaEntityRole.Config,
            HaStateSource.Surface, true, Number("drain_rate", SettingRanges.DrainRateMin,
                SettingRanges.DrainRateMax, "%/h", "mdi:speedometer-slow")),

        // Network. The three adapter readings describe the physical NIC the detection resolved to,
        // never the tunnel or virtual switch above it.
        new(NetworkProfiles, "switch", "Network profiles", HaCategory.Network, HaEntityRole.Config,
            HaStateSource.Surface, true, Switch("network_profiles", ("icon", "mdi:map-marker-radius"))),
        new(UnknownNetworkPreset, "select", "Preset on an unknown network", HaCategory.Network,
            HaEntityRole.Config, HaStateSource.Surface, true,
            Live("unknown_network_preset", ("icon", "mdi:map-marker-question"))),
        new(NetworkAdapterAlias, "sensor", "Network adapter alias", HaCategory.Network, HaEntityRole.Diagnostic,
            HaStateSource.Surface, false, Live("network_alias", ("icon", "mdi:lan-connect"))),
        new(NetworkIpAddress, "sensor", "Network IP address", HaCategory.Network, HaEntityRole.Diagnostic,
            HaStateSource.Surface, false, Live("network_ip", ("icon", "mdi:ip-network"))),
        new(NetworkAdapterName, "sensor", "Network adapter", HaCategory.Network, HaEntityRole.Diagnostic,
            HaStateSource.Surface, false, Live("network_adapter", ("icon", "mdi:expansion-card"))),
        new(NetworkProfileMatched, "sensor", "Network profile", HaCategory.Network, HaEntityRole.Diagnostic,
            HaStateSource.Surface, false, Live("network_profile", ("icon", "mdi:map-marker-check"))),

        // App diagnostics. The startup delay is a real setting, not internal bookkeeping, so it is
        // writable; the downtime gap is the borderline one, published because it changes what the app
        // records rather than only how a window looks.
        new(AppVersion, "sensor", "Version", HaCategory.AppDiagnostics, HaEntityRole.Diagnostic,
            HaStateSource.Surface, false, Live("app_version", ("icon", "mdi:tag-outline"))),
        new(StartupDelay, "number", "Startup delay", HaCategory.AppDiagnostics, HaEntityRole.Config,
            HaStateSource.Surface, true, Number("startup_delay", SettingRanges.StartupDelayMin,
                SettingRanges.StartupDelayMax, "s", "mdi:clock-start")),
        new(IconMode, "select", "Tray icon style", HaCategory.AppDiagnostics, HaEntityRole.Config,
            HaStateSource.Surface, true, Merge(Live("icon_mode", ("icon", "mdi:image-outline")),
                [("options", (object)IconModeOptions)])),
        new(DowntimeGap, "number", "Downtime gap threshold", HaCategory.AppDiagnostics, HaEntityRole.Config,
            HaStateSource.Surface, true, Number("downtime_gap", SettingRanges.DowntimeGapMin,
                SettingRanges.DowntimeGapMax, "min", "mdi:chart-timeline-variant")),
    ];

    /// <summary>Every entity the app knows how to publish, on or off.</summary>
    public static IReadOnlyList<HaEntity> All => _all;

    /// <summary>
    /// The entities a given configuration announces. Pure: the group toggles and the vendor
    /// capabilities decide it, nothing else, so the same answer is reachable from a test, from the
    /// connect sequence and from the eviction pass.
    /// </summary>
    public static IReadOnlyList<HaEntity> Announce(HaCategorySet categories, HaCapabilities capabilities) =>
        [.. _all.Where(e => categories.Includes(e.Category) && IsCapable(e, capabilities))];

    /// <summary>The complement of <see cref="Announce"/>: the entities whose retained discovery must
    /// be emptied, whether they were switched off or the hardware cannot honour them.</summary>
    public static IReadOnlyList<HaEntity> Withheld(HaCategorySet categories, HaCapabilities capabilities) =>
        [.. _all.Where(e => !categories.Includes(e.Category) || !IsCapable(e, capabilities))];

    /// <summary>The vendor gates, one place. A machine with no charge-limit interface announces no
    /// Smart Charge entity at all; one with the discrete BIOS modes keeps the on/off switch but not
    /// the percentages, the preset picker or the one-shot override, none of which it can honour.</summary>
    private static bool IsCapable(HaEntity entity, HaCapabilities capabilities) => entity.Category switch
    {
        HaCategory.SmartCharge => capabilities.SmartCharge switch
        {
            SmartChargeSurface.Hidden  => false,
            SmartChargeSurface.Numeric => true,
            _                          => entity.ObjectId == SmartCharge,
        },
        HaCategory.LidClose => entity.ObjectId == SmartStandby ? capabilities.SmartStandby : capabilities.LidClose,
        _                   => true,
    };
}
