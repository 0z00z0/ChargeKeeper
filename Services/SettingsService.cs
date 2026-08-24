using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChargeKeeper.Services;

internal sealed class ThresholdPreset
{
    public string Name  { get; set; } = "";
    public int    Start { get; set; }
    public int    Stop  { get; set; }

    // Parameterless ctor required for JSON deserialisation.
    public ThresholdPreset() { }
    public ThresholdPreset(string name, int start, int stop)
        { Name = name; Start = start; Stop = stop; }

    /// <summary>Static so a caller holding uncommitted values renders exactly like a saved preset.</summary>
    public static string FormatLabel(string name, int start, int stop) => $"{name}  ({start}–{stop} %)";
}

[JsonConverter(typeof(JsonStringEnumConverter))]
// APPEND new members, never insert: SettingsWindow casts between the ComboBox's SelectedIndex and
// this enum by position, so the two orders have to stay in lockstep.
internal enum TrayIconMode { Arc, Numeric, BrandMark }

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum GraphTimeScale { FifteenMinutes, OneHour, SixHours, TwelveHours, OneDay, OneWeek, FourteenDays }

internal static class GraphTimeScaleExtensions
{
    public static TimeSpan ToTimeSpan(this GraphTimeScale s) => s switch
    {
        GraphTimeScale.FifteenMinutes => TimeSpan.FromMinutes(15),
        GraphTimeScale.OneHour        => TimeSpan.FromHours(1),
        GraphTimeScale.SixHours       => TimeSpan.FromHours(6),
        GraphTimeScale.TwelveHours    => TimeSpan.FromHours(12),
        GraphTimeScale.OneDay         => TimeSpan.FromDays(1),
        GraphTimeScale.OneWeek        => TimeSpan.FromDays(7),
        GraphTimeScale.FourteenDays   => TimeSpan.FromDays(14),
        _                             => TimeSpan.FromHours(1),
    };
}

/// <summary>Persisted application settings.</summary>
internal sealed class AppSettings
{
    public List<ThresholdPreset> Presets { get; set; } =
    [
        new("Daily",  60, 80),
        new("Travel", 80, 100),
    ];

    /// <summary>The one-shot "charge to 100 % once" override, and what to restore when it completes.</summary>
    public bool TravelOverrideActive      { get; set; }
    public int? TravelOverrideRevertStart { get; set; }
    public int? TravelOverrideRevertStop  { get; set; }

    public bool LowBatteryWarningEnabled { get; set; } = true;
    public int  LowBatteryWarningPct     { get; set; } = 15;

    /// <summary>Off by default: on a machine with no charge cap the level reaches 100 % every time
    /// it is left plugged in, and a warning for that is noise rather than news.</summary>
    public bool HighBatteryWarningEnabled { get; set; } = false;
    public int  HighBatteryWarningPct     { get; set; } = 80;

    /// <summary>Normal Modern Standby drain is well under 1 %/hour, so 3 leaves headroom.</summary>
    public bool DrainAnomalyWarningEnabled  { get; set; } = true;
    public int  DrainAnomalyPercentPerHour  { get; set; } = 3;

    public int StartupDelaySeconds { get; set; } = 0;

    public TrayIconMode IconMode { get; set; } = TrayIconMode.Arc;

    public GraphTimeScale GraphTimeScale { get; set; } = GraphTimeScale.OneHour;

    /// <summary>Gap before a hole in the samples is drawn as an axis break. 0 = never, not zero minutes.</summary>
    public int DowntimeGapMinutes { get; set; } = 1;

    /// <summary>The active session is deliberately not persisted — surviving a reboot would surprise.</summary>
    public List<KeepAwakeRequest> KeepAwakePresets { get; set; } =
    [
        new(KeepAwakeKind.Duration,  TimeSpan.FromMinutes(30), null),
        new(KeepAwakeKind.Duration,  TimeSpan.FromHours(1),    null),
        new(KeepAwakeKind.Duration,  TimeSpan.FromHours(3),    null),
        new(KeepAwakeKind.UntilTime, null, new TimeOnly(17, 0)),
    ];

    public bool KeepAwakeDisplayOn { get; set; } = false;

    /// <summary>Never defaulted on: it parks a Windows power setting outside the app for as long as it runs.</summary>
    public bool LidDelayEnabled { get; set; } = false;

    public int LidDelayMinutes { get; set; } = 10;

    /// <summary>On by default, unlike the feature itself: with the lid action parked on "do nothing"
    /// the machine sits awake and unlocked with the lid shut, so the delay removes the sign-in prompt
    /// a lid close normally leads to.</summary>
    public bool LidDelayLockOnClose { get; set; } = true;

    /// <summary>Saved so a restore works even after a crash. Nullable because "do nothing" is index 0
    /// and a legitimate choice, so only null can mean "untouched".</summary>
    public int? LidDelaySavedAcAction { get; set; }
    public int? LidDelaySavedDcAction { get; set; }

    /// <summary>Lid actions are per-scheme, so restoring the indices into a later plan would overwrite
    /// that plan and strand the captured one. Null falls back to the active scheme.</summary>
    public string? LidDelaySavedScheme { get; set; }

    /// <summary>True if either side is stored — a half-written pair still means the scheme was touched.</summary>
    [JsonIgnore]
    public bool HasSavedLidAction => LidDelaySavedAcAction is not null || LidDelaySavedDcAction is not null;

    /// <summary>Master on/off for auto-applying a preset when the detected network location changes.</summary>
    public bool NetworkProfilesEnabled { get; set; } = false;

    public List<NetworkLocationRule> NetworkLocationRules { get; set; } = [];

    /// <summary>
    /// Three-valued on purpose. True once the rules keyed on the routed adapter have been dropped —
    /// persisted, because clearing on every start would also drop the rules saved since. Null means
    /// the key was absent from settings.json, which is a file older than the key or one synced in
    /// from another machine, and reads as "nothing to migrate": absent configuration must never
    /// select the branch that destroys rules. Only an explicit false asks for the migration, and
    /// <see cref="SettingsService.Save"/> stamps null to true so it can be asked for at most once.
    /// </summary>
    public bool? NetworkRulesKeyedOnPhysicalAdapter { get; set; }

    /// <summary>Applied when the location matches no rule. Null = stay put, rather than force a change
    /// on a network the user simply hasn't named yet.</summary>
    public string? UnknownNetworkPresetName { get; set; }

    /// <summary>The single lookup for both the tray status row and the auto-apply, so list order
    /// decides which rule wins in exactly one place.</summary>
    public NetworkLocationRule? FindNetworkRule(NetworkLocation location) =>
        NetworkLocationRules.FirstOrDefault(r => r.Matches(location));

    /// <summary>Inert until this is on AND a broker host is set — ChargeKeeper never touches the network otherwise.</summary>
    public bool HomeAssistantEnabled { get; set; } = false;

    public string MqttBrokerHost { get; set; } = "";

    /// <summary>The broker port, or null to find it. Null by default: the port a broker answers on
    /// depends on the transport and on what fronts it, so assuming 1883 is wrong everywhere except
    /// the plain internal case.</summary>
    public int? MqttBrokerPort { get; set; }

    /// <summary>Set once the port inherited from the days when 1883 was the default has been turned
    /// back into Automatic. Persisted, because a 1883 chosen after that is a choice, not a leftover.</summary>
    public bool MqttPortDefaultRetiredForAutomatic { get; set; }

    public string MqttUsername { get; set; } = "";
    public string MqttPassword { get; set; } = "";
    public bool   MqttUseTls   { get; set; } = false;

    /// <summary>Which transport the broker is reached over. Auto probes; an explicit choice is never
    /// overridden, so a machine pinned to one path fails loudly rather than connecting another way.
    /// The port applies to both transports — see <see cref="MqttTransportEndpoint"/>.</summary>
    public MqttTransportSetting MqttTransportMode { get; set; } = MqttTransportSetting.Auto;

    /// <summary>Where the broker answered last. State rather than a setting: it records where the
    /// machine turned out to be, so a reconnect starts with what worked, and it never changes what
    /// the user chose. Null until something connects, and never a password.</summary>
    public MqttEndpointMemory? MqttLastGoodEndpoint { get; set; }

    /// <summary>Must match HA's own prefix, or discovery configs land where nothing reads them.</summary>
    public string MqttDiscoveryPrefix { get; set; } = "homeassistant";

    /// <summary>Empty = "ChargeKeeper (&lt;machine name&gt;)".</summary>
    public string MqttDeviceName { get; set; } = "";

    /// <summary>The MQTT client id, the <c>unique_id</c> stem, the device identifier and every topic
    /// segment. Empty = derived from the machine name; changing it evicts the old id's retained topics
    /// so HA deletes the previous device instead of ghosting it.</summary>
    public string MqttNodeId { get; set; } = "";

    /// <summary>Which groups of entities are announced. The feature groups are on by default — the
    /// surface is the point of the feature, and a group is switched off to reduce it, never to opt
    /// into it. Turning one off removes its entities from the consumer rather than leaving them
    /// unavailable; see <see cref="HaDiscovery.RemovalTopics"/>.</summary>
    public bool MqttPublishBatteryStatus  { get; set; } = true;
    public bool MqttPublishSmartCharge    { get; set; } = true;
    public bool MqttPublishKeepAwake      { get; set; } = true;
    public bool MqttPublishLidClose       { get; set; } = true;
    public bool MqttPublishNotifications  { get; set; } = true;
    public bool MqttPublishNetwork        { get; set; } = true;

    /// <summary>Off, unlike the feature groups above: diagnostics describe ChargeKeeper rather than
    /// the battery, so they are what an operator opts into rather than what a new install starts
    /// announcing. Existing installs keep whatever they were saved with.</summary>
    public bool MqttPublishAppDiagnostics { get; set; } = false;

    /// <summary>The group toggles as the publisher's pure input.</summary>
    [JsonIgnore]
    public HaCategorySet MqttCategories => new(
        MqttPublishBatteryStatus, MqttPublishSmartCharge, MqttPublishKeepAwake,
        MqttPublishLidClose, MqttPublishNotifications, MqttPublishNetwork, MqttPublishAppDiagnostics);

    /// <summary>Placement in physical pixels, null until the window has been closed once. Not WinUIEx's
    /// PersistenceId, which needs the ApplicationData this unpackaged app lacks.</summary>
    public int? SettingsWindowX      { get; set; }
    public int? SettingsWindowY      { get; set; }
    public int? SettingsWindowWidth  { get; set; }
    public int? SettingsWindowHeight { get; set; }
}

/// <summary>Loads and saves <see cref="AppSettings"/> to <c>%AppData%\ChargeKeeper\settings.json</c> —
/// roaming AppData, so the file follows the user between machines on one profile.</summary>
internal static class SettingsService
{
    private static readonly string _path = AppPaths.DataFile("settings.json");

    private static readonly Lock          _lock = new();
    private static          AppSettings?  _current;

    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented          = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static AppSettings Current
    {
        get { lock (_lock) { return _current ??= ReadFile(_path) ?? new AppSettings(); } }
    }

    public static string FilePath => _path;

    /// <summary>Projects a value out of <see cref="Current"/> under the lock. Needed for anything that
    /// enumerates a collection: <see cref="Update"/> mutates those lists in place, so an unsynchronised
    /// reader can throw "collection was modified".</summary>
    public static T Read<T>(Func<AppSettings, T> project)
    {
        lock (_lock) { return project(_current ??= ReadFile(_path) ?? new AppSettings()); }
    }

    /// <summary>Serialises <see cref="Current"/> to disk. Safe to call from any thread.</summary>
    public static void Save()
    {
        lock (_lock)
        {
            try
            {
                var settings = _current ?? new AppSettings();
                // Stamps the migration marker on the way out, so a file written before the key
                // existed is recorded as needing nothing rather than being read as "not yet
                // migrated" on every later start. Never overwrites an explicit false: that is a
                // pending migration, and the stamp must not pre-empt it.
                settings.NetworkRulesKeyedOnPhysicalAdapter ??= true;

                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                // Atomic write: serialise to a temp file, then replace the target, so a crash
                // mid-write cannot truncate the existing settings.json.
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(settings, _opts));
                File.Move(tmp, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                // Never throws — callers have no return value to check, so this log is the only
                // trace of a setting that did not reach disk.
                AppLog.Error("SettingsService.Save", ex);
            }
        }
    }

    /// <summary>Reads, mutates and saves under one lock acquisition. Prefer this over mutating
    /// <see cref="Current"/> and calling <see cref="Save"/> separately — a <see cref="Reload"/> between
    /// the two silently drops the write.</summary>
    public static void Update(Action<AppSettings> mutate)
    {
        lock (_lock)
        {
            mutate(_current ??= ReadFile(_path) ?? new AppSettings());
            Save();   // re-entrant on the same Lock, so nesting does not deadlock
        }
        Changed?.Invoke();   // outside the lock — a subscriber may do real work (an MQTT publish)
    }

    /// <summary>Raised after any committed change. Services that mirror a setting outwards subscribe
    /// here rather than to each caller, so a new Settings control needs no new notification.</summary>
    public static event Action? Changed;

    /// <summary>Deserialises settings JSON, or null when there is nothing usable. A present-but-unreadable
    /// file is copied aside first, or the next <see cref="Save"/> overwrites the user's presets, network
    /// rules and MQTT credentials with defaults.</summary>
    private static AppSettings? ReadFile(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            if (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), _opts) is { } loaded)
                return loaded;
            PreserveUnreadable(path, "the file contains no settings object");
        }
        catch (Exception ex)
        {
            PreserveUnreadable(path, $"{ex.GetType().Name}: {ex.Message}");
        }
        return null;
    }

    private static void PreserveUnreadable(string path, string reason) =>
        PreserveCopy(path, "unreadable", $"could not be read ({reason}), defaults loaded");

    /// <summary>Copies settings.json aside as <c>settings.json.&lt;tag&gt;-&lt;timestamp&gt;</c>.
    /// Best-effort: callers have nothing to do about a failed copy, so it is logged, never thrown.</summary>
    private static void PreserveCopy(string path, string tag, string reason)
    {
        if (!File.Exists(path)) return;
        string stamp = DateTime.Now.ToString("yyyy-MM-dd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        string copy  = $"{path}.{tag}-{stamp}";
        try
        {
            File.Copy(path, copy, overwrite: true);
            AppLog.Info($"settings.json {reason}; original kept as '{Path.GetFileName(copy)}'.");
        }
        catch (Exception ex)
        {
            AppLog.Error($"SettingsService: settings.json {reason}, and copying it aside as '{tag}' failed", ex);
        }
    }

    /// <summary>
    /// Drops network location rules written before locations were keyed on the physical adapter: those
    /// carry whatever the routing table pointed at, so a VPN's or a virtual switch's MAC and subnet can
    /// stand for several places at once and cannot be mapped back to a NIC. Runs at most once, only
    /// when <see cref="AppSettings.NetworkRulesKeyedOnPhysicalAdapter"/> is explicitly false, and
    /// touches nothing else in settings; settings.json is copied aside first when a rule is going.
    /// </summary>
    public static void ClearRulesKeyedOnTheRoutedAdapter()
    {
        lock (_lock)
        {
            var settings = _current ??= ReadFile(_path) ?? new AppSettings();
            if (settings.NetworkRulesKeyedOnPhysicalAdapter is not false)
            {
                // Absent key: stamp it rather than migrate, so the question is settled for good.
                if (settings.NetworkRulesKeyedOnPhysicalAdapter is null) Save();
                return;
            }

            var adapters = NetworkLocationService.EnumerateAdapters();

            // Copies the file as it still stands on disk, and only when a rule is actually going:
            // ClearRoutedAdapterRules mutates memory alone, and nothing reaches settings.json until
            // Save below. Skipping the empty case is what keeps an earlier copy from being joined by
            // a useless one — the copies are per-second-stamped, never a single overwritten slot.
            if (FindRoutedAdapterRules(settings, adapters).Count > 0)
                PreserveCopy(_path, "backup", "network location rules keyed on a virtual adapter removed");

            int dropped = ClearRoutedAdapterRules(settings, adapters) ?? 0;
            Save();
            AppLog.Info($"Network location rules keyed on the routed adapter removed ({dropped} dropped); "
                      + $"{settings.NetworkLocationRules.Count} kept.");
        }
    }

    /// <summary>
    /// The rules the migration removes: those whose stored MAC belongs to a virtual adapter on this
    /// machine. That is the only positive evidence a key was written against the routed adapter rather
    /// than the NIC behind it, and a rule that cannot be shown to be one is left alone — a rule the
    /// user still wants is worth more than a stale one they can delete.
    /// </summary>
    internal static List<NetworkLocationRule> FindRoutedAdapterRules(
        AppSettings settings, IReadOnlyList<BridgePeer> adapters) =>
        settings.NetworkLocationRules
            .Where(r => NetworkLocationService.IsVirtualAdapterMac(r.AdapterMac, adapters))
            .ToList();

    /// <summary>The decision behind <see cref="ClearRulesKeyedOnTheRoutedAdapter"/>, separated so the
    /// once-only guard is testable: how many rules were removed, or null when the migration is not
    /// being asked for — the marker is true, or absent and therefore not a request.</summary>
    internal static int? ClearRoutedAdapterRules(AppSettings settings, IReadOnlyList<BridgePeer> adapters)
    {
        if (settings.NetworkRulesKeyedOnPhysicalAdapter is not false) return null;
        var doomed = FindRoutedAdapterRules(settings, adapters);
        foreach (var rule in doomed) settings.NetworkLocationRules.Remove(rule);
        settings.NetworkRulesKeyedOnPhysicalAdapter = true;
        return doomed.Count;
    }

    /// <summary>The port the broker setting defaulted to before Automatic existed. A settings.json
    /// written by an older build carries it forward, where it reads as a pinned port.</summary>
    internal const int RetiredDefaultMqttPort = 1883;

    /// <summary>
    /// Turns the inherited 1883 back into Automatic, so an upgraded install gets the detection the
    /// setting was added for instead of staying pinned to the old default. Runs once, and touches
    /// nothing else in settings.
    /// </summary>
    /// <remarks>
    /// settings.json is deliberately not copied aside first. The rule clear above destroys data; this
    /// changes one value that Automatic tries first anyway, so being wrong costs a connect attempt.
    /// </remarks>
    public static void RetireTheDefaultMqttPort()
    {
        lock (_lock)
        {
            var settings = _current ??= ReadFile(_path) ?? new AppSettings();
            if (RetireDefaultMqttPort(settings) is not { } retired) return;

            Save();
            if (retired)
                AppLog.Info($"MQTT broker port {RetiredDefaultMqttPort} was the old default and is now Automatic, "
                          + $"which tries TCP {RetiredDefaultMqttPort} first.");
        }
    }

    /// <summary>The decision behind <see cref="RetireTheDefaultMqttPort"/>, separated so the once-only
    /// guard is testable: true when the port was cleared, false when there was nothing to clear, null
    /// when the migration has already run and must not run again.</summary>
    /// <remarks>
    /// A deliberately pinned 1883 is indistinguishable from the inherited default, so both are cleared.
    /// Safe because Automatic sweeps TCP <see cref="RetiredDefaultMqttPort"/> as its first candidate —
    /// a broker genuinely there still answers on the first attempt.
    /// </remarks>
    internal static bool? RetireDefaultMqttPort(AppSettings settings)
    {
        if (settings.MqttPortDefaultRetiredForAutomatic) return null;
        settings.MqttPortDefaultRetiredForAutomatic = true;

        if (settings.MqttBrokerPort != RetiredDefaultMqttPort) return false;
        settings.MqttBrokerPort = null;
        return true;
    }

    /// <summary>Re-reads settings.json into <see cref="Current"/>, discarding unsaved changes, so an
    /// out-of-band edit is picked up without a restart. Returns false and leaves <see cref="Current"/>
    /// untouched on a missing or invalid file; never writes back.</summary>
    public static bool Reload()
    {
        if (ReadFile(_path) is not { } loaded) return false;
        lock (_lock) { _current = loaded; }
        Reloaded?.Invoke();   // outside the lock — a subscriber may do real work (an MQTT reconnect)
        return true;
    }

    /// <summary>Services holding their own copy of a setting must reconcile here, or they keep running
    /// on the pre-reload value.</summary>
    public static event Action? Reloaded;
}
