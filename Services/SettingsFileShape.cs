using System.Text.Json;
using System.Text.Json.Serialization;
using ZeroZero.Mqtt;

namespace ChargeKeeper.Services;

/// <summary>
/// The on-disk shape of <c>settings.json</c>: one object per Settings page, in the order the pages
/// run in the navigation pane, and within each object the order the rows appear on that page.
/// Finding a setting in the file means looking where it sits on screen.
/// </summary>
/// <remarks>
/// A serialisation shape rather than the in-memory model: <see cref="AppSettings"/> stays flat, so
/// no call site changes and the grouping cannot drift into behaviour. Group keys reuse
/// <see cref="MqttPublishGroups"/> wherever a page has one there; <c>graph</c>, <c>mqtt</c> and
/// <c>window</c> name pages or state that vocabulary does not cover.
/// Property order is pinned with <see cref="JsonPropertyOrderAttribute"/> on every member:
/// System.Text.Json orders unattributed members by reflection order, which is not a guarantee.
/// </remarks>
internal sealed class SettingsFile
{
    /// <summary>The grouped shape. Absent from a file means the flat shape that preceded it, which
    /// is why the number starts at 1 rather than 0.</summary>
    public const int CurrentVersion = 1;

    public const string VersionKey      = "version";
    public const string GeneralKey      = "general";
    public const string GraphKey        = "graph";
    public const string SmartChargeKey  = MqttPublishGroups.SmartCharge;
    public const string NetworkKey      = MqttPublishGroups.Network;
    public const string KeepAwakeKey    = MqttPublishGroups.KeepAwake;
    public const string LidCloseKey     = MqttPublishGroups.LidClose;
    public const string NotificationsKey = MqttPublishGroups.Notifications;
    public const string MqttKey         = "mqtt";
    public const string DiagnosticsKey  = "diagnostics";
    public const string AppearanceKey   = "appearance";
    public const string WindowKey       = "window";

    /// <summary>First key in the file, so the shape is read rather than inferred.</summary>
    [JsonPropertyName(VersionKey), JsonPropertyOrder(0)]
    public int Version { get; set; } = CurrentVersion;

    [JsonPropertyName(GeneralKey), JsonPropertyOrder(1)]
    public GeneralGroup General { get; set; } = new();

    [JsonPropertyName(GraphKey), JsonPropertyOrder(2)]
    public GraphGroup Graph { get; set; } = new();

    [JsonPropertyName(SmartChargeKey), JsonPropertyOrder(3)]
    public SmartChargeGroup SmartCharge { get; set; } = new();

    // Network profiles have no navigation item: the rows sit on the Smart Charge page below the
    // presets, so the group follows it here too.
    [JsonPropertyName(NetworkKey), JsonPropertyOrder(4)]
    public NetworkGroup Network { get; set; } = new();

    [JsonPropertyName(KeepAwakeKey), JsonPropertyOrder(5)]
    public KeepAwakeGroup KeepAwake { get; set; } = new();

    [JsonPropertyName(LidCloseKey), JsonPropertyOrder(6)]
    public LidCloseGroup LidClose { get; set; } = new();

    [JsonPropertyName(NotificationsKey), JsonPropertyOrder(7)]
    public NotificationsGroup Notifications { get; set; } = new();

    [JsonPropertyName(MqttKey), JsonPropertyOrder(8)]
    public MqttGroup Mqtt { get; set; } = new();

    [JsonPropertyName(DiagnosticsKey), JsonPropertyOrder(9)]
    public DiagnosticsGroup Diagnostics { get; set; } = new();

    [JsonPropertyName(AppearanceKey), JsonPropertyOrder(10)]
    public AppearanceGroup Appearance { get; set; } = new();

    // Window placement is state rather than a page: nothing on screen edits it, so it sits last.
    [JsonPropertyName(WindowKey), JsonPropertyOrder(11)]
    public WindowGroup Window { get; set; } = new();

    internal sealed class GeneralGroup
    {
        [JsonPropertyOrder(1)] public int          StartupDelaySeconds { get; set; }
        [JsonPropertyOrder(2)] public TrayIconMode IconMode            { get; set; }
    }

    internal sealed class GraphGroup
    {
        [JsonPropertyOrder(1)] public GraphTimeScale     GraphTimeScale      { get; set; }
        [JsonPropertyOrder(2)] public GraphLineColouring GraphLineColouring  { get; set; }
        [JsonPropertyOrder(3)] public bool               GraphShadingEnabled { get; set; }
        [JsonPropertyOrder(4)] public int                DowntimeGapMinutes  { get; set; }
        [JsonPropertyOrder(5)] public GraphDisplay       GraphDisplay        { get; set; }
    }

    internal sealed class SmartChargeGroup
    {
        [JsonPropertyOrder(1)] public List<ThresholdPreset> Presets                   { get; set; } = [];
        [JsonPropertyOrder(2)] public bool                  TravelOverrideActive      { get; set; }
        [JsonPropertyOrder(3)] public int?                  TravelOverrideRevertStart { get; set; }
        [JsonPropertyOrder(4)] public int?                  TravelOverrideRevertStop  { get; set; }
    }

    internal sealed class NetworkGroup
    {
        [JsonPropertyOrder(1)] public bool                      NetworkProfilesEnabled   { get; set; }
        [JsonPropertyOrder(2)] public List<NetworkLocationRule> NetworkLocationRules     { get; set; } = [];
        [JsonPropertyOrder(3)] public string?                   UnknownNetworkPresetName { get; set; }
        [JsonPropertyOrder(4)] public bool? NetworkRulesKeyedOnPhysicalAdapter { get; set; }
    }

    internal sealed class KeepAwakeGroup
    {
        [JsonPropertyOrder(1)] public bool                   KeepAwakeDisplayOn { get; set; }
        [JsonPropertyOrder(2)] public List<KeepAwakeRequest> KeepAwakePresets   { get; set; } = [];
    }

    internal sealed class LidCloseGroup
    {
        [JsonPropertyOrder(1)] public bool                      LidDelayEnabled           { get; set; }
        [JsonPropertyOrder(2)] public bool                      LidDelayOffAfterSleep     { get; set; }
        [JsonPropertyOrder(3)] public bool                      LidDelayLockOnClose       { get; set; }
        // Nullable so a file written before the clock became one condition of two reads as "on",
        // which is the only thing it can have meant; a plain bool would read as off and silently
        // drop the delay such a file was relying on.
        [JsonPropertyOrder(4)] public bool?                     LidDelayTimeEnabled       { get; set; }
        [JsonPropertyOrder(5)] public int                       LidDelayMinutes           { get; set; }
        // Nullable for the same reason: absent means the built-in delays, empty means a list the
        // user emptied.
        [JsonPropertyOrder(6)] public List<LidDelayPreset>?     LidDelayPresets           { get; set; }
        [JsonPropertyOrder(7)] public bool                      LidDischargeEnabled       { get; set; }
        [JsonPropertyOrder(8)] public int                       LidDischargeTargetPercent { get; set; }
        [JsonPropertyOrder(9)] public List<LidDischargeTarget>  LidDischargePresets       { get; set; } = [];
        // Saved power-scheme state, edited by nothing on the page, so it trails the visible rows.
        [JsonPropertyOrder(10)] public int?    LidDelaySavedAcAction { get; set; }
        [JsonPropertyOrder(11)] public int?    LidDelaySavedDcAction { get; set; }
        [JsonPropertyOrder(12)] public string? LidDelaySavedScheme   { get; set; }
    }

    internal sealed class NotificationsGroup
    {
        [JsonPropertyOrder(1)] public int  LowBatteryWarningPct        { get; set; }
        [JsonPropertyOrder(2)] public bool LowBatteryWarningEnabled    { get; set; }
        [JsonPropertyOrder(3)] public int  HighBatteryWarningPct       { get; set; }
        [JsonPropertyOrder(4)] public bool HighBatteryWarningEnabled   { get; set; }
        [JsonPropertyOrder(5)] public int  DrainAnomalyPercentPerHour  { get; set; }
        [JsonPropertyOrder(6)] public bool DrainAnomalyWarningEnabled  { get; set; }
    }

    internal sealed class MqttGroup
    {
        [JsonPropertyOrder(1)] public MqttEndpointMemory? MqttLastGoodEndpoint { get; set; }
    }

    internal sealed class DiagnosticsGroup
    {
        [JsonPropertyOrder(1)] public bool                  PerformanceGraphEnabled { get; set; }
        [JsonPropertyOrder(2)] public PerformanceSampleRate PerformanceSampleRate   { get; set; }
    }

    internal sealed class AppearanceGroup
    {
        [JsonPropertyOrder(1)] public bool OneLineUntilItMatters { get; set; }
    }

    internal sealed class WindowGroup
    {
        [JsonPropertyOrder(1)] public int? SettingsWindowX      { get; set; }
        [JsonPropertyOrder(2)] public int? SettingsWindowY      { get; set; }
        [JsonPropertyOrder(3)] public int? SettingsWindowWidth  { get; set; }
        [JsonPropertyOrder(4)] public int? SettingsWindowHeight { get; set; }
    }

    public static SettingsFile From(AppSettings s) => new()
    {
        General = new GeneralGroup
        {
            StartupDelaySeconds = s.StartupDelaySeconds,
            IconMode            = s.IconMode,
        },
        Graph = new GraphGroup
        {
            GraphTimeScale      = s.GraphTimeScale,
            GraphLineColouring  = s.GraphLineColouring,
            GraphShadingEnabled = s.GraphShadingEnabled,
            DowntimeGapMinutes  = s.DowntimeGapMinutes,
            GraphDisplay        = s.GraphDisplay,
        },
        SmartCharge = new SmartChargeGroup
        {
            Presets                   = s.Presets,
            TravelOverrideActive      = s.TravelOverrideActive,
            TravelOverrideRevertStart = s.TravelOverrideRevertStart,
            TravelOverrideRevertStop  = s.TravelOverrideRevertStop,
        },
        Network = new NetworkGroup
        {
            NetworkProfilesEnabled             = s.NetworkProfilesEnabled,
            NetworkLocationRules               = s.NetworkLocationRules,
            UnknownNetworkPresetName           = s.UnknownNetworkPresetName,
            NetworkRulesKeyedOnPhysicalAdapter = s.NetworkRulesKeyedOnPhysicalAdapter,
        },
        KeepAwake = new KeepAwakeGroup
        {
            KeepAwakeDisplayOn = s.KeepAwakeDisplayOn,
            KeepAwakePresets   = s.KeepAwakePresets,
        },
        LidClose = new LidCloseGroup
        {
            LidDelayEnabled           = s.LidDelayEnabled,
            LidDelayOffAfterSleep     = s.LidDelayOffAfterSleep,
            LidDelayLockOnClose       = s.LidDelayLockOnClose,
            LidDelayTimeEnabled       = s.LidDelayTimeEnabled,
            LidDelayMinutes           = s.LidDelayMinutes,
            LidDelayPresets           = s.LidDelayPresets,
            LidDischargeEnabled       = s.LidDischargeEnabled,
            LidDischargeTargetPercent = s.LidDischargeTargetPercent,
            LidDischargePresets       = s.LidDischargePresets,
            LidDelaySavedAcAction     = s.LidDelaySavedAcAction,
            LidDelaySavedDcAction     = s.LidDelaySavedDcAction,
            LidDelaySavedScheme       = s.LidDelaySavedScheme,
        },
        Notifications = new NotificationsGroup
        {
            LowBatteryWarningPct       = s.LowBatteryWarningPct,
            LowBatteryWarningEnabled   = s.LowBatteryWarningEnabled,
            HighBatteryWarningPct      = s.HighBatteryWarningPct,
            HighBatteryWarningEnabled  = s.HighBatteryWarningEnabled,
            DrainAnomalyPercentPerHour = s.DrainAnomalyPercentPerHour,
            DrainAnomalyWarningEnabled = s.DrainAnomalyWarningEnabled,
        },
        Mqtt        = new MqttGroup { MqttLastGoodEndpoint = s.MqttLastGoodEndpoint },
        Diagnostics = new DiagnosticsGroup
        {
            PerformanceGraphEnabled = s.PerformanceGraphEnabled,
            PerformanceSampleRate   = s.PerformanceSampleRate,
        },
        Appearance = new AppearanceGroup
        {
            OneLineUntilItMatters = s.OneLineUntilItMatters,
        },
        Window = new WindowGroup
        {
            SettingsWindowX      = s.SettingsWindowX,
            SettingsWindowY      = s.SettingsWindowY,
            SettingsWindowWidth  = s.SettingsWindowWidth,
            SettingsWindowHeight = s.SettingsWindowHeight,
        },
    };

    public AppSettings ToSettings() => new()
    {
        StartupDelaySeconds = General.StartupDelaySeconds,
        IconMode            = General.IconMode,

        GraphTimeScale      = Graph.GraphTimeScale,
        GraphLineColouring  = Graph.GraphLineColouring,
        GraphShadingEnabled = Graph.GraphShadingEnabled,
        DowntimeGapMinutes  = Graph.DowntimeGapMinutes,
        GraphDisplay        = Graph.GraphDisplay,

        Presets                   = SmartCharge.Presets,
        TravelOverrideActive      = SmartCharge.TravelOverrideActive,
        TravelOverrideRevertStart = SmartCharge.TravelOverrideRevertStart,
        TravelOverrideRevertStop  = SmartCharge.TravelOverrideRevertStop,

        NetworkProfilesEnabled             = Network.NetworkProfilesEnabled,
        NetworkLocationRules               = Network.NetworkLocationRules,
        UnknownNetworkPresetName           = Network.UnknownNetworkPresetName,
        NetworkRulesKeyedOnPhysicalAdapter = Network.NetworkRulesKeyedOnPhysicalAdapter,

        KeepAwakeDisplayOn = KeepAwake.KeepAwakeDisplayOn,
        KeepAwakePresets   = KeepAwake.KeepAwakePresets,

        LidDelayEnabled           = LidClose.LidDelayEnabled,
        LidDelayOffAfterSleep     = LidClose.LidDelayOffAfterSleep,
        LidDelayLockOnClose       = LidClose.LidDelayLockOnClose,
        LidDelayTimeEnabled       = LidClose.LidDelayTimeEnabled ?? true,
        LidDelayMinutes           = LidClose.LidDelayMinutes,
        LidDelayPresets           = LidClose.LidDelayPresets ?? new AppSettings().LidDelayPresets,
        LidDischargeEnabled       = LidClose.LidDischargeEnabled,
        LidDischargeTargetPercent = LidClose.LidDischargeTargetPercent,
        LidDischargePresets       = LidClose.LidDischargePresets,
        LidDelaySavedAcAction     = LidClose.LidDelaySavedAcAction,
        LidDelaySavedDcAction     = LidClose.LidDelaySavedDcAction,
        LidDelaySavedScheme       = LidClose.LidDelaySavedScheme,

        LowBatteryWarningPct       = Notifications.LowBatteryWarningPct,
        LowBatteryWarningEnabled   = Notifications.LowBatteryWarningEnabled,
        HighBatteryWarningPct      = Notifications.HighBatteryWarningPct,
        HighBatteryWarningEnabled  = Notifications.HighBatteryWarningEnabled,
        DrainAnomalyPercentPerHour = Notifications.DrainAnomalyPercentPerHour,
        DrainAnomalyWarningEnabled = Notifications.DrainAnomalyWarningEnabled,

        MqttLastGoodEndpoint = Mqtt.MqttLastGoodEndpoint,

        PerformanceGraphEnabled = Diagnostics.PerformanceGraphEnabled,
        PerformanceSampleRate   = Diagnostics.PerformanceSampleRate,

        OneLineUntilItMatters = Appearance.OneLineUntilItMatters,

        SettingsWindowX      = Window.SettingsWindowX,
        SettingsWindowY      = Window.SettingsWindowY,
        SettingsWindowWidth  = Window.SettingsWindowWidth,
        SettingsWindowHeight = Window.SettingsWindowHeight,
    };

    /// <summary>The shape of a file, read from its version key rather than inferred from which keys
    /// it happens to carry. Null means the key is absent or not a whole number, which is the flat
    /// shape written before this version existed.</summary>
    public static int? ReadVersion(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(VersionKey, out var v) &&
        v.ValueKind == JsonValueKind.Number &&
        v.TryGetInt32(out int version)
            ? version
            : null;
}
