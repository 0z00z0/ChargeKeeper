using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChargeKeeper.Services;

/// <summary>A named charging-threshold profile.</summary>
internal sealed class ThresholdPreset
{
    public string Name  { get; set; } = "";
    public int    Start { get; set; }
    public int    Stop  { get; set; }

    // Parameterless ctor required for JSON deserialisation.
    public ThresholdPreset() { }
    public ThresholdPreset(string name, int start, int stop)
        { Name = name; Start = start; Stop = stop; }

    /// <summary>
    /// How a preset renders as a one-line label — the single source shared by the tray Presets
    /// submenu (<c>TrayMenu.PresetLabel</c>) and the Settings window's preset rows so the two can't
    /// drift. Static overload for callers holding not-yet-committed name/start/stop values.
    /// </summary>
    public static string FormatLabel(string name, int start, int stop) => $"{name}  ({start}–{stop} %)";

    /// <summary>This preset rendered via <see cref="FormatLabel(string,int,int)"/>.</summary>
    public string Label => FormatLabel(Name, Start, Stop);
}

/// <summary>Tray icon rendering mode.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum TrayIconMode { Arc, Numeric }

/// <summary>Selected time span for the dashboard history graph.</summary>
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
    // ── Threshold presets ────────────────────────────────────────────────────
    /// <summary>Named presets shown in the Presets submenu.</summary>
    public List<ThresholdPreset> Presets { get; set; } =
    [
        new("Daily",  60, 80),
        new("Travel", 80, 100),
    ];

    /// <summary>Name of the active preset, or null when a custom threshold is in use.</summary>
    public string? ActivePreset { get; set; }

    // ── Travel override ──────────────────────────────────────────────────────
    /// <summary>True while a one-shot "charge to 100 % once" override is in progress.</summary>
    public bool TravelOverrideActive      { get; set; }
    /// <summary>Threshold to restore when the override completes (null = leave disabled).</summary>
    public int? TravelOverrideRevertStart { get; set; }
    public int? TravelOverrideRevertStop  { get; set; }

    // ── Notifications ────────────────────────────────────────────────────────
    public bool LowBatteryWarningEnabled { get; set; } = true;
    public int  LowBatteryWarningPct     { get; set; } = 15;

    /// <summary>
    /// Overnight-drain anomaly warning (TODO #26): toast when the battery loses charge faster than
    /// this rate across a detected downtime gap (app closed, crashed, or the system suspended) —
    /// e.g. Modern Standby failing to actually suspend can drain several percent an hour even with
    /// the lid closed. Default 3%/hour is a deliberately loose bar: normal Modern Standby drain is
    /// usually well under 1%/hour, so 3 leaves headroom before flagging genuinely abnormal drain.
    /// </summary>
    public bool DrainAnomalyWarningEnabled  { get; set; } = true;
    public int  DrainAnomalyPercentPerHour  { get; set; } = 3;

    // ── App behaviour ────────────────────────────────────────────────────────
    /// <summary>Seconds to pause at startup before initialising (0 = no delay).</summary>
    public int StartupDelaySeconds { get; set; } = 0;

    /// <summary>Arc gauge (default) or numeric % in the tray icon.</summary>
    public TrayIconMode IconMode { get; set; } = TrayIconMode.Arc;

    // ── History graph ────────────────────────────────────────────────────────
    /// <summary>Selected time span shown in the dashboard history graph.</summary>
    public GraphTimeScale GraphTimeScale { get; set; } = GraphTimeScale.OneHour;

    /// <summary>
    /// How long a hole in the sample timeline must be before it's registered as a downtime gap
    /// (app closed/crashed) and drawn as a compressed-axis break instead of a connecting line.
    /// Default of 1 matches the previous hardcoded ~1-minute threshold
    /// (<c>BatteryHistoryService.SampleIntervalSeconds * 3</c>), so existing users see no change in
    /// behaviour until they pick a different value. 0 = "None" — disable gap detection entirely
    /// (never show a gap marker, treated as an effectively infinite threshold, NOT a literal
    /// zero-minute one).
    /// </summary>
    public int DowntimeGapMinutes { get; set; } = 1;

    // ── Keep awake (issue #90) ──────────────────────────────────────────────────
    /// <summary>
    /// The one-click keep-awake spans. The ACTIVE SESSION is never persisted — only these presets and
    /// <see cref="KeepAwakeDisplayOn"/> — because keep-awake surviving a reboot would be a surprise.
    /// </summary>
    public List<KeepAwakeRequest> KeepAwakePresets { get; set; } =
    [
        new(KeepAwakeKind.Duration,  TimeSpan.FromMinutes(30), null),
        new(KeepAwakeKind.Duration,  TimeSpan.FromHours(1),    null),
        new(KeepAwakeKind.Duration,  TimeSpan.FromHours(3),    null),
        new(KeepAwakeKind.UntilTime, null, new TimeOnly(17, 0)),
    ];

    /// <summary>
    /// Also keep the DISPLAY on while a keep-awake session runs. Off by default: the common case is a
    /// long build or download finishing, where the screen may sleep as usual.
    /// </summary>
    public bool KeepAwakeDisplayOn { get; set; } = false;

    // ── Lid-close delay (issue #90) ─────────────────────────────────────────────
    /// <summary>
    /// Wait <see cref="LidDelayMinutes"/> after the lid closes before sleeping. OFF by default and
    /// never defaulted on: turning it on changes a Windows power setting OUTSIDE the app (the
    /// lid-close action, parked on "do nothing" for as long as it runs), which is only ever the
    /// user's call to make. See <see cref="LidDelayService"/> for why nothing lighter works.
    /// </summary>
    public bool LidDelayEnabled { get; set; } = false;

    /// <summary>How long to hold the machine awake after the lid closes, in minutes.</summary>
    public int LidDelayMinutes { get; set; } = 10;

    /// <summary>
    /// Lock the workstation at lid close while <see cref="LidDelayEnabled"/> holds the machine awake.
    /// <para>ON by default, unlike the feature itself. With the lid-close action parked on "do nothing"
    /// the machine sits awake and UNLOCKED with the lid shut, so the delay removes the sign-in prompt a
    /// lid close normally leads to. That is a protection the machine already had, and losing it must not
    /// depend on the user noticing.</para>
    /// </summary>
    public bool LidDelayLockOnClose { get; set; } = true;

    /// <summary>
    /// The user's OWN lid-close action indices, saved before the feature overrode them, so they can be
    /// put back — including after a crash, which is the failure mode this pair exists for.
    /// <para>NULLABLE on purpose: "do nothing" is index 0 and is a value the user may legitimately have
    /// set themselves, so a plain int could not tell that apart from "nothing was ever saved" and the
    /// restore would skip them. Null means the power scheme is untouched.</para>
    /// </summary>
    public int? LidDelaySavedAcAction { get; set; }
    public int? LidDelaySavedDcAction { get; set; }

    /// <summary>
    /// WHICH power scheme the saved indices came from. Lid actions are per-scheme, so the values are
    /// meaningless without it: restoring them into whatever plan is active later would overwrite that
    /// plan's setting and leave the captured one still parked on "do nothing". Null on a settings file
    /// written before this was tracked — the restore then falls back to the active scheme.
    /// </summary>
    public string? LidDelaySavedScheme { get; set; }

    /// <summary>
    /// Whether the lid-close action was overridden and the user's own value is still owed back. True
    /// if EITHER side is stored: a half-written pair still means the power scheme was touched, so it
    /// must drive a restore rather than being read as clean.
    /// </summary>
    [JsonIgnore]
    public bool HasSavedLidAction => LidDelaySavedAcAction is not null || LidDelaySavedDcAction is not null;

    // ── Network / dock-based profiles (TODO #31) ────────────────────────────────
    /// <summary>Master on/off for auto-applying a preset when the detected network location changes.</summary>
    public bool NetworkProfilesEnabled { get; set; } = false;

    /// <summary>User-configured location → preset mappings ("office dock" → "Daily", etc.).</summary>
    public List<NetworkLocationRule> NetworkLocationRules { get; set; } = [];

    /// <summary>
    /// Preset to apply when the current location matches none of <see cref="NetworkLocationRules"/>
    /// — the "unknown network → travel preset" case from the original idea. Null = do nothing (stay
    /// on whatever threshold was already active) rather than force a change on every unrecognised
    /// network, which would be surprising on a network the user simply hasn't gotten round to
    /// naming yet.
    /// </summary>
    public string? UnknownNetworkPresetName { get; set; }

    /// <summary>
    /// First rule matching <paramref name="location"/>, or null. The single lookup both the tray
    /// menu's "Current: …" status row and the location-change auto-apply use, so "which rule wins"
    /// (list order) stays defined in exactly one place.
    /// </summary>
    public NetworkLocationRule? FindNetworkRule(NetworkLocation location) =>
        NetworkLocationRules.FirstOrDefault(r => r.Matches(location));

    // ── Home Assistant / MQTT (TODO #28) ────────────────────────────────────────
    /// <summary>
    /// Master on/off for the MQTT publisher. Off by default and inert until the user both enables it
    /// AND fills in a broker host — ChargeKeeper never touches the network otherwise.
    /// </summary>
    public bool HomeAssistantEnabled { get; set; } = false;

    /// <summary>MQTT broker hostname/IP (e.g. the Home Assistant host). Empty = feature inactive.</summary>
    public string MqttBrokerHost { get; set; } = "";

    /// <summary>MQTT broker port. 1883 plaintext / 8883 TLS by convention.</summary>
    public int MqttBrokerPort { get; set; } = 1883;

    /// <summary>MQTT username (blank for an anonymous broker).</summary>
    public string MqttUsername { get; set; } = "";

    /// <summary>
    /// MQTT password (blank for an anonymous broker). Stored in the user's own local settings.json,
    /// same as any MQTT client's config — it is the user's broker credential, entered by the user.
    /// </summary>
    public string MqttPassword { get; set; } = "";

    /// <summary>Use TLS for the broker connection (port is usually 8883 then).</summary>
    public bool MqttUseTls { get; set; } = false;

    /// <summary>
    /// Home Assistant MQTT-discovery topic prefix — must match HA's configured prefix (default
    /// "homeassistant"). Discovery config topics are published under this; entity state under
    /// "chargekeeper/&lt;node&gt;/…".
    /// </summary>
    public string MqttDiscoveryPrefix { get; set; } = "homeassistant";

    /// <summary>
    /// Friendly name of the device Home Assistant creates (issue #87). Empty = "ChargeKeeper
    /// (&lt;machine name&gt;)", so an existing install is byte-identical after upgrading.
    /// </summary>
    public string MqttDeviceName { get; set; } = "";

    /// <summary>
    /// Node id override (issue #87) — this is the MQTT client id, the <c>unique_id</c>/<c>object_id</c>
    /// stem, the device identifier AND every topic segment. Empty = derived from the machine name
    /// (<see cref="HaDiscovery.NodeId"/>). Changing it actively evicts the old id's retained topics so
    /// HA deletes the previous device instead of leaving a ghost beside the new one.
    /// </summary>
    public string MqttNodeId { get; set; } = "";

    // ── Settings window (TODO #19) ──────────────────────────────────────────────
    /// <summary>
    /// Last on-screen position/size of the Settings window, in physical pixels — restored on next
    /// open, ignored/clamped if it would land off every current monitor (e.g. a monitor was
    /// unplugged). Null until the window has been closed at least once. WinUIEx's own
    /// PersistenceId is NOT used here: it stores through Windows.Storage.ApplicationData, which is
    /// unavailable to this unpackaged app — persisting through settings.json instead keeps it
    /// consistent with every other piece of app state.
    /// </summary>
    public int? SettingsWindowX      { get; set; }
    public int? SettingsWindowY      { get; set; }
    public int? SettingsWindowWidth  { get; set; }
    public int? SettingsWindowHeight { get; set; }

    // TODO #45: a future Appearance/styling setting would live here (the earlier no-op
    // `UseNewStyling` toggle and its dead Settings section were removed).
}

/// <summary>
/// Loads and saves <see cref="AppSettings"/> to
/// <c>%AppData%\ChargeKeeper\settings.json</c>.
/// <para>
/// Roaming AppData syncs automatically via Windows roaming profiles and OneDrive
/// Known Folder Move — the file follows the user between machines on the same profile.
/// It is plain human-readable JSON, so it can also be copied or backed up manually.
/// </para>
/// </summary>
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

    /// <summary>The loaded (and potentially modified) settings instance.</summary>
    public static AppSettings Current
    {
        get { lock (_lock) { return _current ??= ReadFile(_path) ?? new AppSettings(); } }
    }

    /// <summary>Path to the settings file — surfaced in UI as "Open settings file".</summary>
    public static string FilePath => _path;

    /// <summary>Serialises <see cref="Current"/> to disk. Safe to call from any thread.</summary>
    public static void Save()
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                // Atomic write: serialise to a temp file, then replace the target. A crash
                // mid-write can't truncate or corrupt the existing settings.json this way.
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp,
                    JsonSerializer.Serialize(_current ?? new AppSettings(), _opts));
                File.Move(tmp, _path, overwrite: true);
            }
            catch { /* Save failure must not crash the app. */ }
        }
    }

    /// <summary>
    /// Atomically reads, mutates, and saves <see cref="Current"/> under one lock acquisition.
    /// Use this — never the older "var s = Current; ... s.Prop = x; Save();" pattern — for any
    /// mutation that spans an async gap (a background Task, an await, an RPC round-trip): the older
    /// pattern captures a REFERENCE to the settings object, and if <see cref="Reload"/> swaps
    /// <see cref="Current"/> out from under it during that gap, the mutation lands on an orphaned
    /// object while <see cref="Save"/> (which always serialises the live <c>_current</c>, not the
    /// stale capture) persists the reloaded object unchanged — silently dropping the write. Nesting
    /// under the same <see cref="Lock"/> instance is safe: <c>System.Threading.Lock</c> (like the
    /// classic <c>lock</c> keyword) is re-entrant for the thread already holding it, so calling
    /// <see cref="Save"/> from inside this method's own lock does not deadlock.
    /// </summary>
    public static void Update(Action<AppSettings> mutate)
    {
        lock (_lock)
        {
            mutate(_current ??= ReadFile(_path) ?? new AppSettings());
            Save();
        }
    }

    /// <summary>
    /// Deserialises settings JSON from <paramref name="path"/>, or null when there is nothing usable
    /// to load. "Missing" (first run — defaults are correct) and "present but unreadable" are handled
    /// separately: for the second, the file is COPIED aside and the failure logged before defaults are
    /// returned. Without that, the next <see cref="Save"/> overwrites the user's presets, network rules
    /// and MQTT credentials with defaults, and the only record of them is gone.
    /// </summary>
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

    /// <summary>
    /// Copies an unreadable settings file aside so the defaults-then-Save path cannot destroy it, and
    /// logs both the reason and where the copy went. Best-effort: a failed copy is logged, never thrown
    /// (this runs under the load path, which must not take the app down).
    /// </summary>
    private static void PreserveUnreadable(string path, string reason)
    {
        string stamp = DateTime.Now.ToString("yyyy-MM-dd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        string copy  = $"{path}.unreadable-{stamp}";
        try
        {
            File.Copy(path, copy, overwrite: true);
            AppLog.Info($"settings.json could not be read ({reason}); defaults loaded, original kept as '{Path.GetFileName(copy)}'.");
        }
        catch (Exception ex)
        {
            AppLog.Error($"SettingsService: settings.json unreadable ({reason}) and the aside copy failed", ex);
        }
    }

    /// <summary>
    /// Re-reads settings.json from disk into <see cref="Current"/>, discarding any in-memory
    /// changes that were never saved — the "Reload settings from file" menu command, for picking
    /// up an out-of-band edit (e.g. a manually-edited file, or one synced in from another machine
    /// via roaming/OneDrive) without restarting the app. Never writes back to <see cref="FilePath"/> —
    /// it's a read-only refresh of the canonical file. Leaves <see cref="Current"/> untouched and
    /// returns false on a missing or invalid file.
    /// </summary>
    public static bool Reload()
    {
        if (ReadFile(_path) is not { } loaded) return false;
        lock (_lock) { _current = loaded; }
        Reloaded?.Invoke();   // outside the lock — a subscriber may do real work (an MQTT reconnect)
        return true;
    }

    /// <summary>
    /// Raised after <see cref="Reload"/> successfully swaps <see cref="Current"/>. Services that hold
    /// their own copy of a setting must reconcile here: the Settings window's reload only refreshes
    /// what it DISPLAYS, so without this an out-of-band edit to (say) the MQTT node id sat in
    /// <see cref="Current"/> while the live client kept publishing under the previous one.
    /// </summary>
    public static event Action? Reloaded;
}
