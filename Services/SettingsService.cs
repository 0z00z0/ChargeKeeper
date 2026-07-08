using System.Text.Json;
using System.Text.Json.Serialization;

namespace LenovoTray.Services;

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

    // ── App behaviour ────────────────────────────────────────────────────────
    /// <summary>Seconds to pause at startup before initialising (0 = no delay).</summary>
    public int StartupDelaySeconds { get; set; } = 0;

    /// <summary>Arc gauge (default) or numeric % in the tray icon.</summary>
    public TrayIconMode IconMode { get; set; } = TrayIconMode.Arc;

    // ── History graph ────────────────────────────────────────────────────────
    /// <summary>Selected time span shown in the dashboard history graph.</summary>
    public GraphTimeScale GraphTimeScale { get; set; } = GraphTimeScale.OneHour;
}

/// <summary>
/// Loads and saves <see cref="AppSettings"/> to
/// <c>%AppData%\LenovoPowerTray\settings.json</c>.
/// <para>
/// Roaming AppData syncs automatically via Windows roaming profiles and OneDrive
/// Known Folder Move — the file follows the user between machines on the same profile.
/// It is plain human-readable JSON, so it can also be copied or backed up manually.
/// </para>
/// </summary>
internal static class SettingsService
{
    private static readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LenovoPowerTray", "settings.json");

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

    /// <summary>Deserialises settings JSON from <paramref name="path"/>. Null on any I/O/parse failure or a missing file.</summary>
    private static AppSettings? ReadFile(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), _opts)
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Re-reads settings.json from disk into <see cref="Current"/>, discarding any in-memory
    /// changes that were never saved — the "Reload settings from disk" menu command, for picking
    /// up an out-of-band edit (e.g. a manually-edited file, or one synced in from another machine
    /// via roaming/OneDrive) without restarting the app. Unlike <see cref="Import"/>, this never
    /// writes back to <see cref="FilePath"/> — it's a read-only refresh of the canonical file, not
    /// a load-from-elsewhere-and-adopt. Leaves <see cref="Current"/> untouched and returns false on
    /// a missing or invalid file.
    /// </summary>
    public static bool Reload()
    {
        if (ReadFile(_path) is not { } loaded) return false;
        lock (_lock) { _current = loaded; }
        return true;
    }

    /// <summary>Writes the current settings to an arbitrary path (Export). Throws on I/O error.</summary>
    public static void Export(string path)
    {
        lock (_lock)
            File.WriteAllText(path, JsonSerializer.Serialize(_current ?? new AppSettings(), _opts));
    }

    /// <summary>
    /// Loads settings from an arbitrary path and makes them the live, persisted settings (Import).
    /// Returns false on a missing/invalid file. The caller is responsible for refreshing any UI
    /// that reflects settings (the dashboard re-reads on the next show; icon mode via ForceIconRefresh).
    /// </summary>
    public static bool Import(string path)
    {
        if (ReadFile(path) is not { } loaded) return false;
        lock (_lock)
        {
            _current = loaded;
            Save();   // persist the imported settings to the canonical location
        }
        return true;
    }
}
