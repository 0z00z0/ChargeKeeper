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
        get { lock (_lock) { return _current ??= Load(); } }
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

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), _opts)
                       ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
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
        try
        {
            if (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), _opts) is not { } loaded)
                return false;

            lock (_lock)
                _current = loaded;

            Save();   // persist the imported settings to the canonical location
            return true;
        }
        catch
        {
            return false;
        }
    }
}
