using System.Globalization;
using ZeroZero.Mqtt;
using ZeroZero.Mqtt.Discovery;

namespace ChargeKeeper.Services;

/// <summary>Everything the entity table reads and writes, in one object initialiser. Supplied by the
/// publisher at runtime and by a spy in a test, so the whole table composes with no broker, no vendor
/// hardware and no settings file.</summary>
internal sealed record MqttEntitySources
{
    /// <summary>The live battery and charge-control snapshot, or null before the first reading.</summary>
    public required Func<LiveState?> Live { get; init; }

    /// <summary>The settings, network and diagnostic snapshot, or null when it cannot be taken.</summary>
    public required Func<SurfaceState?> Surface { get; init; }

    /// <summary>The vendor gates the announcement is filtered through. <b>Throws rather than
    /// answering</b> when a vendor read fails: the announcement layer reads a throw as "could not be
    /// read" and keeps the disposition already recorded, while a false says the capability is absent
    /// and withholds every entity behind it.</summary>
    public required Func<PublishCapabilities> Capabilities { get; init; }

    public required IChargeControlActions Charge { get; init; }

    public required ISettingsActions Settings { get; init; }
}

/// <summary>
/// ChargeKeeper's published surface: forty-six entities, their groups, their capability gates and
/// the domain seam each inbound command lands on. Pure — nothing here touches a broker or a settings
/// singleton, so the same table composes in a test.
/// </summary>
/// <remarks>An entity id is the <c>unique_id</c> stem after the device id, so it carries the entity
/// across every rename, regrouping and topic move the receiver ever sees. Change one and the user
/// loses the name, the entity id, the area, the labels and the automations attached to it. They do
/// not change.</remarks>
internal static class MqttEntityCatalog
{
    // Entity ids. Shared so the table, the tests and the migration name each entity identically.

    public const string BatteryLevel        = "battery_level";
    public const string BatteryState        = "battery_state";
    public const string BatteryPower        = "battery_power";
    public const string BatteryHealth       = "battery_health";
    public const string IsCharging          = "is_charging";
    public const string OnAc                = "on_ac";
    public const string PowerState          = "power_state";
    public const string RemainingChargeTime = "remaining_charge_time";
    public const string AdapterWatts        = "adapter_watts";
    public const string CapacityFull        = "capacity_full";
    public const string CapacityDesign      = "capacity_design";
    public const string LowPowerMode        = "low_power_mode";

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

    public const string LidDelay              = "lid_delay";
    public const string LidDelayTime          = "lid_delay_time";
    public const string LidDelayMinutes       = "lid_delay_minutes";
    public const string LidDelayLock          = "lid_delay_lock";
    public const string LidDelayOffAfterSleep = "lid_delay_off_after_sleep";
    public const string LidDischarge          = "lid_discharge";
    public const string LidDischargePercent   = "lid_discharge_percent";
    public const string SmartStandby          = "smart_standby";

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

    /// <summary>The tray icon styles, spelled as the enum so a round trip needs no lookup table.</summary>
    public static readonly string[] IconModeOptions =
        [nameof(TrayIconMode.Arc), nameof(TrayIconMode.Numeric), nameof(TrayIconMode.BrandMark)];

    /// <summary>Entities an earlier version published under a different component or id. Their
    /// retained per-component configs sit at paths nothing composes any more, so each is emptied once
    /// and written down. Declared in source and kept indefinitely: an installation upgrading from
    /// before an entry was added still carries the ghost it evicts.</summary>
    public static IReadOnlyList<RetiredEntity> Retired { get; } =
    [
        new("sensor",        "soc"),          // → sensor/battery_level
        new("sensor",        "power"),        // → sensor/battery_power
        new("binary_sensor", "smart_charge"), // → switch/smart_charge
        new("sensor",        "charge_start"), // → number/charge_start
        new("sensor",        "charge_stop"),  // → number/charge_stop
    ];

    /// <summary>The two value topics an earlier version published its shared JSON payloads on. One
    /// bare topic per entity replaced them, so nothing composes these any more and the retained
    /// payloads would stand on the broker indefinitely. The module empties each once per identity and
    /// records the composed topic in the ledger.</summary>
    /// <remarks>Declared in source and kept indefinitely, as <see cref="Retired"/> is: an installation
    /// upgrading from before an entry was added still carries the payload it empties.</remarks>
    public static IReadOnlyList<RetiredChannel> RetiredChannels { get; } =
    [
        new("state"),
        new("status"),
    ];

    /// <summary>
    /// The forty entities as they stood on the broker before the device document existed: one
    /// retained single-component config each. The handover keeps every one of them — name, entity id,
    /// icon, area, labels and registry id — and empties the old topic afterwards.
    /// </summary>
    /// <remarks><b>Frozen. This list must never grow.</b> It describes what an upgrading installation
    /// already has, not what ChargeKeeper publishes: an entity added after the document was adopted
    /// never had a single-component config, so declaring it here would hand over a topic nothing ever
    /// wrote and then empty it, once, for nothing.</remarks>
    public static IReadOnlyList<MigratingEntity> Migrating { get; } =
    [
        new("sensor",        BatteryLevel),
        new("sensor",        BatteryState),
        new("sensor",        BatteryPower),
        new("binary_sensor", IsCharging),
        new("binary_sensor", OnAc),
        new("sensor",        BatteryHealth),
        new("sensor",        RemainingChargeTime),
        new("sensor",        AdapterWatts),
        new("sensor",        CapacityFull),
        new("sensor",        CapacityDesign),
        new("switch",        SmartCharge),
        new("number",        ChargeStart),
        new("number",        ChargeStop),
        new("button",        ChargeToFull),
        new("select",        Preset),
        new("binary_sensor", TravelOverride),
        new("switch",        KeepAwake),
        new("text",          KeepAwakeFor),
        new("sensor",        KeepAwakeExpires),
        new("switch",        KeepAwakeDisplayOn),
        new("switch",        LidDelay),
        new("number",        LidDelayMinutes),
        new("switch",        LidDelayLock),
        new("switch",        SmartStandby),
        new("switch",        LowBatteryWarning),
        new("number",        LowBatteryLevel),
        new("switch",        HighBatteryWarning),
        new("number",        HighBatteryLevel),
        new("switch",        DrainWarning),
        new("number",        DrainRate),
        new("switch",        NetworkProfiles),
        new("select",        UnknownNetworkPreset),
        new("sensor",        NetworkAdapterAlias),
        new("sensor",        NetworkIpAddress),
        new("sensor",        NetworkAdapterName),
        new("sensor",        NetworkProfileMatched),
        new("sensor",        AppVersion),
        new("number",        StartupDelay),
        new("select",        IconMode),
        new("number",        DowntimeGap),
    ];

    // Deliberately absent, because the value means nothing outside this process: the Settings window's
    // saved placement, the last-selected graph scale, the travel override's revert pair (the override
    // itself is published), the lid-action values captured for crash recovery, and the once-only
    // network-rule migration flag. The broker block is absent for a different reason — it describes
    // the transport rather than the machine, and its credentials are a secret. The saved lists —
    // presets, keep-awake presets, network rules — reach the surface as the two selects' options and
    // the matched-profile sensor rather than as entities of their own.

    /// <summary>The whole table, bound to one set of sources.</summary>
    /// <exception cref="ArgumentException">Two entities share an id, or an id is not topic-safe.</exception>
    public static MqttEntitySet Build(MqttEntitySources s)
    {
        ArgumentNullException.ThrowIfNull(s);

        var live = s.Live;
        var surface = s.Surface;
        var charge = s.Charge;
        var set = s.Settings;

        return new MqttEntitySet(
        [
            // ── Battery status ───────────────────────────────────────────────────────────────────
            // The five a dashboard would show stay uncategorised; the derived readings and the raw
            // capacities are diagnostics — health is the answer, the capacities are its workings.
            new MqttSensor
            {
                EntityId = BatteryLevel, Name = "Battery level", Group = MqttPublishGroups.BatteryStatus,
                DeviceClass = "battery", Unit = "%", StateClass = MqttStateClass.Measurement,
                Read = () => MqttPayload.Number((long?)live()?.Soc),
            },
            new MqttSensor
            {
                EntityId = BatteryState, Name = "Battery state", Group = MqttPublishGroups.BatteryStatus,
                Icon = "mdi:battery-charging",
                Read = () => live()?.BatteryState,
            },
            new MqttSensor
            {
                EntityId = BatteryPower, Name = "Battery power", Group = MqttPublishGroups.BatteryStatus,
                DeviceClass = "power", Unit = "W", StateClass = MqttStateClass.Measurement,
                // Positive = charging/input, negative = draining, as the firmware reports it.
                Read = () => MqttPayload.Number(LiveStateBuilder.Watts(live()?.PowerMw)),
            },
            new MqttBinarySensor
            {
                EntityId = IsCharging, Name = "Is charging", Group = MqttPublishGroups.BatteryStatus,
                DeviceClass = "battery_charging",
                Read = () => live()?.IsCharging,
            },
            new MqttBinarySensor
            {
                EntityId = OnAc, Name = "On AC", Group = MqttPublishGroups.BatteryStatus,
                DeviceClass = "plug",
                Read = () => live()?.OnAc,
            },
            new MqttSensor
            {
                // The two mains states told apart, which neither is_charging nor on_ac says on its
                // own: a pack held at a charge limit reads off and on. Read-only — the state is what
                // the firmware is doing, and no command changes it. Derived like health is, so it is
                // a diagnostic rather than a sixth uncategorised reading.
                EntityId = PowerState, Name = "Battery power state", Group = MqttPublishGroups.BatteryStatus,
                Category = MqttEntityCategory.Diagnostic, Icon = "mdi:power-plug-battery",
                Read = () => live() is { } v
                    ? Helpers.PowerStates.Label(Helpers.PowerStates.From(v.IsCharging, v.OnAc))
                    : null,
            },
            new MqttSensor
            {
                EntityId = BatteryHealth, Name = "Battery health", Group = MqttPublishGroups.BatteryStatus,
                Category = MqttEntityCategory.Diagnostic, Icon = "mdi:heart-pulse",
                Read = () => live()?.Health,
            },
            new MqttSensor
            {
                EntityId = RemainingChargeTime, Name = "Battery remaining charge time",
                Group = MqttPublishGroups.BatteryStatus, Category = MqttEntityCategory.Diagnostic,
                DeviceClass = "duration", Unit = "min", Icon = "mdi:timer-sand",
                Read = () => MqttPayload.Number((long?)live()?.RemainingMinutes),
            },
            new MqttSensor
            {
                EntityId = AdapterWatts, Name = "Adapter rating", Group = MqttPublishGroups.BatteryStatus,
                Category = MqttEntityCategory.Diagnostic, DeviceClass = "power", Unit = "W",
                Read = () => MqttPayload.Number((long?)live()?.AdapterWatts),
            },
            new MqttSensor
            {
                EntityId = CapacityFull, Name = "Battery full-charge capacity", Group = MqttPublishGroups.BatteryStatus,
                Category = MqttEntityCategory.Diagnostic, DeviceClass = "energy_storage", Unit = "Wh",
                Read = () => MqttPayload.Number(LiveStateBuilder.WattHours(live()?.FullMwh)),
            },
            new MqttSensor
            {
                EntityId = CapacityDesign, Name = "Battery design capacity", Group = MqttPublishGroups.BatteryStatus,
                Category = MqttEntityCategory.Diagnostic, DeviceClass = "energy_storage", Unit = "Wh",
                Read = () => MqttPayload.Number(LiveStateBuilder.WattHours(live()?.DesignMwh)),
            },
            new MqttBinarySensor
            {
                // Windows Energy Saver, read-only: the OS owns the switch. No device class fits it,
                // so the icon carries the meaning. Not in Migrating — an earlier version carried this
                // as a json_attributes key on battery_state, never as a config topic of its own.
                EntityId = LowPowerMode, Name = "Low power mode", Group = MqttPublishGroups.BatteryStatus,
                Category = MqttEntityCategory.Diagnostic, Icon = "mdi:leaf",
                Read = () => live()?.LowPowerMode,
            },

            // ── Smart Charge ─────────────────────────────────────────────────────────────────────
            new MqttSwitch
            {
                EntityId = SmartCharge, Name = "Smart Charge", Group = MqttPublishGroups.SmartCharge,
                Icon = "mdi:battery-heart-variant", Debounce = MqttConnection.ReflectDebounce,
                Include = () => SmartChargeGate(s, SmartCharge),
                Read = () => live()?.SmartChargeEnabled,
                Apply = on => MqttCommandVerdict.Accept(() => charge.SetSmartChargeEnabled(on)),
            },
            new MqttNumber
            {
                EntityId = ChargeStart, Name = "Charge threshold start", Group = MqttPublishGroups.SmartCharge,
                Category = MqttEntityCategory.Config, Unit = "%", Icon = "mdi:battery-arrow-up",
                Min = PresetEditValidator.MinThreshold, Max = PresetEditValidator.MaxThreshold,
                Mode = MqttNumberMode.Slider, Debounce = MqttConnection.ReflectDebounce,
                Include = () => SmartChargeGate(s, ChargeStart),
                Read = () => live()?.ChargeStart,
                Apply = value => MqttCommandVerdict.Accept(() =>
                {
                    var (_, stop) = charge.CurrentThresholds();
                    var pair = ChargeThresholdCommands.WithStart(Whole(value), stop);
                    charge.ApplyThresholds(pair.Start, pair.Stop);
                }),
            },
            new MqttNumber
            {
                EntityId = ChargeStop, Name = "Charge threshold end", Group = MqttPublishGroups.SmartCharge,
                Category = MqttEntityCategory.Config, Unit = "%", Icon = "mdi:battery-arrow-down",
                Min = PresetEditValidator.MinThreshold, Max = PresetEditValidator.MaxThreshold,
                Mode = MqttNumberMode.Slider, Debounce = MqttConnection.ReflectDebounce,
                Include = () => SmartChargeGate(s, ChargeStop),
                Read = () => live()?.ChargeStop,
                Apply = value => MqttCommandVerdict.Accept(() =>
                {
                    var (start, _) = charge.CurrentThresholds();
                    var pair = ChargeThresholdCommands.WithStop(Whole(value), start);
                    charge.ApplyThresholds(pair.Start, pair.Stop);
                }),
            },
            new MqttButton
            {
                EntityId = ChargeToFull, Name = "Charge to 100 % once", Group = MqttPublishGroups.SmartCharge,
                Icon = "mdi:battery-charging-100",
                Include = () => SmartChargeGate(s, ChargeToFull),
                Press = () => MqttCommandVerdict.Accept(charge.ChargeToFullOnce),
            },
            new MqttSelect
            {
                EntityId = Preset, Name = "Charge preset", Group = MqttPublishGroups.SmartCharge,
                Icon = "mdi:playlist-check", Debounce = MqttConnection.ReflectDebounce,
                // Withheld rather than announced empty when nothing is configured: an empty option
                // list is not a schema the receiver accepts, and the state is reversible the moment a
                // preset is saved.
                Include = () => SmartChargeGate(s, Preset) && set.PresetNames().Count > 0,
                Options = set.PresetNames,
                Read = () => live()?.ActivePreset,
                Apply = name => MqttCommandVerdict.Accept(() => charge.ApplyPreset(name)),
            },
            new MqttBinarySensor
            {
                EntityId = TravelOverride, Name = "Charge to full in progress", Group = MqttPublishGroups.SmartCharge,
                Category = MqttEntityCategory.Diagnostic, Icon = "mdi:airplane",
                Include = () => SmartChargeGate(s, TravelOverride),
                Read = () => surface()?.TravelOverrideActive,
            },

            // ── Keep Awake ───────────────────────────────────────────────────────────────────────
            // The expiry is published as an instant rather than a countdown, so a running session does
            // not re-publish once a minute.
            new MqttSwitch
            {
                EntityId = KeepAwake, Name = "Keep awake", Group = MqttPublishGroups.KeepAwake,
                Icon = "mdi:coffee", Debounce = MqttConnection.ReflectDebounce,
                Read = () => surface()?.KeepAwakeActive,
                Apply = on => MqttCommandVerdict.Accept(() => set.SetKeepAwake(on)),
            },
            new MqttText
            {
                EntityId = KeepAwakeFor, Name = "Keep awake for", Group = MqttPublishGroups.KeepAwake,
                Category = MqttEntityCategory.Config, Icon = "mdi:timer-cog-outline", MaxLength = 16,
                Debounce = MqttConnection.ReflectDebounce,
                Read = () => surface()?.KeepAwakeFor,
                // The same parser the Settings box uses, so "1h30", "17:00" and "45" mean here exactly
                // what they mean there, and everything else is refused rather than guessed at.
                Apply = text => KeepAwakeInputParser.TryParse(text, out var request)
                    ? MqttCommandVerdict.Accept(() => set.StartKeepAwake(request))
                    : MqttCommandVerdict.Malformed("Expected a duration like '90', '1h30' or a clock time like '17:00'."),
            },
            new MqttSensor
            {
                EntityId = KeepAwakeExpires, Name = "Keep awake until", Group = MqttPublishGroups.KeepAwake,
                Category = MqttEntityCategory.Diagnostic, DeviceClass = "timestamp",
                // A timestamp sensor wants a full ISO 8601 instant with an offset; no session with a
                // clock expiry means no value at all.
                Read = () => surface()?.KeepAwakeExpires?.ToString("o", CultureInfo.InvariantCulture),
            },
            new MqttSwitch
            {
                // Named to sort as one block under the other Keep entries. The entity id stays put:
                // moving it discards an installation's entity ids, areas, labels and automations.
                EntityId = KeepAwakeDisplayOn, Name = "Keep awake with the screen on", Group = MqttPublishGroups.KeepAwake,
                Category = MqttEntityCategory.Config, Icon = "mdi:monitor-shimmer",
                Debounce = MqttConnection.ReflectDebounce,
                Read = () => surface()?.KeepAwakeDisplayOn,
                Apply = on => MqttCommandVerdict.Accept(() => set.SetKeepAwakeDisplayOn(on)),
            },

            // ── Lid close, and the standby scheduling the dashboard pairs with it ────────────────
            // The master switch keeps its entity id: it is what "lid handling is on" has always meant
            // to an installation, and moving it would discard every entity registration built on it.
            // The clock it used to imply is now a condition of its own, on a new id.
            new MqttSwitch
            {
                EntityId = LidDelay, Name = "Lid-close active", Group = MqttPublishGroups.LidClose,
                Category = MqttEntityCategory.Config, Icon = "mdi:laptop",
                Debounce = MqttConnection.ReflectDebounce,
                Include = () => s.Capabilities().LidClose,
                Read = () => surface()?.LidDelayEnabled,
                Apply = on => MqttCommandVerdict.Accept(() => set.SetLidDelay(on)),
            },
            new MqttSwitch
            {
                EntityId = LidDelayTime, Name = "Lid-close delay", Group = MqttPublishGroups.LidClose,
                Category = MqttEntityCategory.Config, Icon = "mdi:timer-outline",
                Debounce = MqttConnection.ReflectDebounce,
                Include = () => s.Capabilities().LidClose,
                Read = () => surface()?.LidDelayTimeEnabled,
                Apply = on => MqttCommandVerdict.Accept(() => set.SetLidDelayTime(on)),
            },
            new MqttNumber
            {
                EntityId = LidDelayMinutes, Name = "Lid-close delay length", Group = MqttPublishGroups.LidClose,
                Category = MqttEntityCategory.Config, Unit = "min", Icon = "mdi:timer-outline",
                Min = LidDelayPolicy.MinMinutes, Max = LidDelayPolicy.MaxMinutes,
                Mode = MqttNumberMode.Box, Debounce = MqttConnection.ReflectDebounce,
                Include = () => s.Capabilities().LidClose,
                Read = () => surface()?.LidDelayMinutes,
                Apply = value => MqttCommandVerdict.Accept(() => set.SetLidDelayMinutes(Whole(value))),
            },
            new MqttSwitch
            {
                EntityId = LidDischarge, Name = "Lid-close battery target", Group = MqttPublishGroups.LidClose,
                Category = MqttEntityCategory.Config, Icon = "mdi:battery-arrow-down",
                Debounce = MqttConnection.ReflectDebounce,
                Include = () => s.Capabilities().LidClose,
                Read = () => surface()?.LidDischargeEnabled,
                Apply = on => MqttCommandVerdict.Accept(() => set.SetLidDischarge(on)),
            },
            new MqttNumber
            {
                EntityId = LidDischargePercent, Name = "Lid-close battery target level",
                Group = MqttPublishGroups.LidClose,
                Category = MqttEntityCategory.Config, Unit = "%", Icon = "mdi:battery-arrow-down",
                Min = LidDischargeWatch.MinPercent, Max = LidDischargeWatch.MaxPercent,
                Mode = MqttNumberMode.Slider, Debounce = MqttConnection.ReflectDebounce,
                Include = () => s.Capabilities().LidClose,
                Read = () => surface()?.LidDischargeTargetPercent,
                Apply = value => MqttCommandVerdict.Accept(() => set.SetLidDischargePercent(Whole(value))),
            },
            new MqttSwitch
            {
                EntityId = LidDelayLock, Name = "Lid-close lock", Group = MqttPublishGroups.LidClose,
                Category = MqttEntityCategory.Config, Icon = "mdi:lock",
                Debounce = MqttConnection.ReflectDebounce,
                Include = () => s.Capabilities().LidClose,
                Read = () => surface()?.LidDelayLockOnClose,
                Apply = on => MqttCommandVerdict.Accept(() => set.SetLidDelayLock(on)),
            },
            new MqttSwitch
            {
                EntityId = LidDelayOffAfterSleep, Name = "Lid-close off after sleeping",
                Group = MqttPublishGroups.LidClose,
                Category = MqttEntityCategory.Config, Icon = "mdi:numeric-1-box-outline",
                Debounce = MqttConnection.ReflectDebounce,
                Include = () => s.Capabilities().LidClose,
                Read = () => surface()?.LidDelayOffAfterSleep,
                Apply = on => MqttCommandVerdict.Accept(() => set.SetLidDelayOffAfterSleep(on)),
            },
            new MqttSwitch
            {
                EntityId = SmartStandby, Name = "Smart Standby", Group = MqttPublishGroups.LidClose,
                Icon = "mdi:sleep", Debounce = MqttConnection.ReflectDebounce,
                Include = () => s.Capabilities().SmartStandby,
                Read = () => surface()?.SmartStandbyRunning,
                Apply = on => MqttCommandVerdict.Accept(() => set.SetSmartStandby(on)),
            },

            // ── Notifications ────────────────────────────────────────────────────────────────────
            new MqttSwitch
            {
                EntityId = LowBatteryWarning, Name = "Notify low battery", Group = MqttPublishGroups.Notifications,
                Category = MqttEntityCategory.Config, Icon = "mdi:battery-alert",
                Debounce = MqttConnection.ReflectDebounce,
                Read = () => surface()?.LowBatteryWarning,
                Apply = on => MqttCommandVerdict.Accept(() => set.SetLowBatteryWarning(on)),
            },
            new MqttNumber
            {
                EntityId = LowBatteryLevel, Name = "Notify low battery level", Group = MqttPublishGroups.Notifications,
                Category = MqttEntityCategory.Config, Unit = "%", Icon = "mdi:battery-low",
                Min = SettingRanges.LowBatteryMin, Max = SettingRanges.LowBatteryMax,
                Mode = MqttNumberMode.Box, Debounce = MqttConnection.ReflectDebounce,
                Read = () => surface()?.LowBatteryLevel,
                Apply = value => MqttCommandVerdict.Accept(() => set.SetLowBatteryLevel(Whole(value))),
            },
            new MqttSwitch
            {
                EntityId = HighBatteryWarning, Name = "Notify high battery", Group = MqttPublishGroups.Notifications,
                Category = MqttEntityCategory.Config, Icon = "mdi:battery-alert-variant",
                Debounce = MqttConnection.ReflectDebounce,
                Read = () => surface()?.HighBatteryWarning,
                Apply = on => MqttCommandVerdict.Accept(() => set.SetHighBatteryWarning(on)),
            },
            new MqttNumber
            {
                EntityId = HighBatteryLevel, Name = "Notify high battery level", Group = MqttPublishGroups.Notifications,
                Category = MqttEntityCategory.Config, Unit = "%", Icon = "mdi:battery-high",
                Min = SettingRanges.HighBatteryMin, Max = SettingRanges.HighBatteryMax,
                Mode = MqttNumberMode.Box, Debounce = MqttConnection.ReflectDebounce,
                Read = () => surface()?.HighBatteryLevel,
                Apply = value => MqttCommandVerdict.Accept(() => set.SetHighBatteryLevel(Whole(value))),
            },
            new MqttSwitch
            {
                EntityId = DrainWarning, Name = "Notify standby drain", Group = MqttPublishGroups.Notifications,
                Category = MqttEntityCategory.Config, Icon = "mdi:battery-clock",
                Debounce = MqttConnection.ReflectDebounce,
                Read = () => surface()?.DrainWarning,
                Apply = on => MqttCommandVerdict.Accept(() => set.SetDrainWarning(on)),
            },
            new MqttNumber
            {
                EntityId = DrainRate, Name = "Notify standby drain rate", Group = MqttPublishGroups.Notifications,
                Category = MqttEntityCategory.Config, Unit = "%/h", Icon = "mdi:speedometer-slow",
                Min = SettingRanges.DrainRateMin, Max = SettingRanges.DrainRateMax,
                Mode = MqttNumberMode.Box, Debounce = MqttConnection.ReflectDebounce,
                Read = () => surface()?.DrainRate,
                Apply = value => MqttCommandVerdict.Accept(() => set.SetDrainRate(Whole(value))),
            },

            // ── Network ──────────────────────────────────────────────────────────────────────────
            // The three adapter readings describe the physical NIC the detection resolved to, never
            // the tunnel or virtual switch above it.
            new MqttSwitch
            {
                EntityId = NetworkProfiles, Name = "Network profiles", Group = MqttPublishGroups.Network,
                Category = MqttEntityCategory.Config, Icon = "mdi:map-marker-radius",
                Debounce = MqttConnection.ReflectDebounce,
                Read = () => surface()?.NetworkProfilesEnabled,
                Apply = on => MqttCommandVerdict.Accept(() => set.SetNetworkProfiles(on)),
            },
            new MqttSelect
            {
                EntityId = UnknownNetworkPreset, Name = "Network fallback preset",
                Group = MqttPublishGroups.Network, Category = MqttEntityCategory.Config,
                Icon = "mdi:map-marker-question", Debounce = MqttConnection.ReflectDebounce,
                // The sentinel leads, so the picker always has at least one option and "stay put"
                // is a choice rather than an absence.
                Options = () => [PresetEditValidator.UnknownNetworkSentinel, .. set.PresetNames()],
                Read = () => surface()?.UnknownNetworkPreset,
                Apply = name => MqttCommandVerdict.Accept(() => set.SetUnknownNetworkPreset(
                    string.Equals(name, PresetEditValidator.UnknownNetworkSentinel, StringComparison.Ordinal)
                        ? null
                        : name)),
            },
            new MqttSensor
            {
                EntityId = NetworkAdapterAlias, Name = "Network adapter alias", Group = MqttPublishGroups.Network,
                Category = MqttEntityCategory.Diagnostic, Icon = "mdi:lan-connect",
                Read = () => Known(surface()?.NetworkAlias),
            },
            new MqttSensor
            {
                EntityId = NetworkIpAddress, Name = "Network IP address", Group = MqttPublishGroups.Network,
                Category = MqttEntityCategory.Diagnostic, Icon = "mdi:ip-network",
                Read = () => Known(surface()?.NetworkIpAddress),
            },
            new MqttSensor
            {
                EntityId = NetworkAdapterName, Name = "Network adapter", Group = MqttPublishGroups.Network,
                Category = MqttEntityCategory.Diagnostic, Icon = "mdi:expansion-card",
                Read = () => Known(surface()?.NetworkAdapterName),
            },
            new MqttSensor
            {
                EntityId = NetworkProfileMatched, Name = "Network profile", Group = MqttPublishGroups.Network,
                Category = MqttEntityCategory.Diagnostic, Icon = "mdi:map-marker-check",
                // Matched nothing is a reading, not an absence, so it publishes the literal rather
                // than falling through to the reset one.
                Read = () => surface() is { } v ? v.MatchedNetworkProfile ?? SurfaceReader.NoProfile : null,
            },

            // ── App diagnostics ──────────────────────────────────────────────────────────────────
            // The startup delay is a real setting, not internal bookkeeping, so it is writable; the
            // downtime gap is the borderline one, published because it changes what the app records
            // rather than only how a window looks.
            new MqttSensor
            {
                EntityId = AppVersion, Name = "App version", Group = MqttPublishGroups.AppDiagnostics,
                Category = MqttEntityCategory.Diagnostic, Icon = "mdi:tag-outline",
                Read = () => surface()?.AppVersion,
            },
            new MqttNumber
            {
                EntityId = StartupDelay, Name = "App startup delay", Group = MqttPublishGroups.AppDiagnostics,
                Category = MqttEntityCategory.Config, Unit = "s", Icon = "mdi:clock-start",
                Min = SettingRanges.StartupDelayMin, Max = SettingRanges.StartupDelayMax,
                Mode = MqttNumberMode.Box, Debounce = MqttConnection.ReflectDebounce,
                Read = () => surface()?.StartupDelaySeconds,
                Apply = value => MqttCommandVerdict.Accept(() => set.SetStartupDelay(Whole(value))),
            },
            new MqttSelect
            {
                EntityId = IconMode, Name = "App tray icon style", Group = MqttPublishGroups.AppDiagnostics,
                Category = MqttEntityCategory.Config, Icon = "mdi:image-outline",
                Debounce = MqttConnection.ReflectDebounce,
                Options = () => IconModeOptions,
                Read = () => surface()?.IconMode.ToString(),
                // The option list is the enum's own member names, so a value that reached Accept
                // parses; the guard is against a member removed from the enum but left in the list.
                Apply = name => Enum.TryParse<TrayIconMode>(name, ignoreCase: true, out var mode)
                                && Enum.IsDefined(mode)
                    ? MqttCommandVerdict.Accept(() => set.SetIconMode(mode))
                    : MqttCommandVerdict.NotAnOption($"'{name}' is not a tray icon style."),
            },
            new MqttNumber
            {
                EntityId = DowntimeGap, Name = "App downtime gap threshold", Group = MqttPublishGroups.AppDiagnostics,
                Category = MqttEntityCategory.Config, Unit = "min", Icon = "mdi:chart-timeline-variant",
                Min = SettingRanges.DowntimeGapMin, Max = SettingRanges.DowntimeGapMax,
                Mode = MqttNumberMode.Box, Debounce = MqttConnection.ReflectDebounce,
                Read = () => surface()?.DowntimeGapMinutes,
                Apply = value => MqttCommandVerdict.Accept(() => set.SetDowntimeGap(Whole(value))),
            },
        ]);
    }

    /// <summary>The Smart Charge gate, one place. A machine with no charge-limit interface announces
    /// no Smart Charge entity at all; one with the discrete BIOS modes keeps the on/off switch but not
    /// the percentages, the preset picker or the one-shot override, none of which it can honour.</summary>
    /// <remarks>Reads the capability through the source, so a vendor read that throws propagates as a
    /// throw and the announcement keeps whatever the record already says.</remarks>
    internal static bool SmartChargeGate(MqttEntitySources sources, string entityId) =>
        sources.Capabilities().SmartCharge switch
        {
            SmartChargeSurface.Hidden  => false,
            SmartChargeSurface.Numeric => true,
            _                          => entityId == SmartCharge,
        };

    /// <summary>An inbound number as the whole one every ChargeKeeper setting is. Already inside the
    /// entity's declared bounds by the time it arrives, so this only drops a fractional part a
    /// receiver's own control could not have produced.</summary>
    private static int Whole(double value) => (int)Math.Round(value);

    /// <summary>A reading, or null when there is nothing to report. An empty string is an absent
    /// value for every one of these, not a value of its own, and a receiver ignores an empty payload
    /// on a sensor — so it has to reach the entity as null and go out as the reset literal.</summary>
    private static string? Known(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
