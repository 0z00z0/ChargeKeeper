using System.Globalization;

namespace ChargeKeeper.Services;

/// <summary>One recorded battery reading. Power is stored in milliwatts (positive = charging).</summary>
internal readonly record struct BatterySample(DateTime AtUtc, int Soc, int? LimitPct, int PowerMw);

/// <summary>
/// File-backed battery history. Every sample (SoC %, Smart-Charge limit %, charge power mW) is
/// appended to <c>%AppData%\ChargeKeeper\history.csv</c> with an absolute UTC timestamp, so the
/// graph survives restarts and downtime shows up as a gap in the timeline. Data is kept for 14 days.
/// <para>
/// Only the <em>currently-selected time window</em> is held in memory: <see cref="LoadWindow"/> reads
/// that slice from the file, and <see cref="Record"/> keeps it trimmed as new samples arrive.
/// </para>
/// </summary>
internal static class BatteryHistoryService
{
    private const int RetentionDays = 14;

    /// <summary>
    /// Expected interval between samples (must match the period App's history-sampling timer
    /// runs on). Shared so the dashboard's gap-detection threshold is derived from this single
    /// constant instead of a second, independently-hardcoded number that could drift out of sync.
    /// </summary>
    public const int SampleIntervalSeconds = 20;

    private static readonly string _defaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ChargeKeeper", "history.csv");
    private static string _path = _defaultPath;

    private static readonly Lock _lock = new();

    // The slice currently loaded for the dashboard, oldest → newest.
    private static readonly List<BatterySample> _window = [];
    private static TimeSpan _windowSpan = TimeSpan.FromHours(1);
    private static bool _pruned;
    private static bool _dirEnsured;

    /// <summary>Path to the CSV history file — can be surfaced in the UI for backup/inspection.</summary>
    public static string FilePath => _path;

    /// <summary>The time span currently loaded into memory (set by the last <see cref="LoadWindow"/> call).</summary>
    public static TimeSpan CurrentSpan { get { lock (_lock) return _windowSpan; } }

    /// <summary>
    /// TEST-ONLY seam: points the service at an isolated file and resets all in-memory state.
    /// Needed because every member here is static, so it otherwise persists for the whole
    /// process — without this, one test's data would leak into the next.
    /// </summary>
    internal static void UseTestPath(string path)
    {
        lock (_lock)
        {
            _path = path;
            _window.Clear();
            _windowSpan = TimeSpan.FromHours(1);
            _pruned = false;
            _dirEnsured = false;
        }
    }

    /// <summary>Appends a sample to the file and to the in-memory window. Thread-safe; never throws.</summary>
    public static void Record(int soc, int? limitPct, int powerMw)
    {
        var sample = new BatterySample(DateTime.UtcNow, soc, limitPct, powerMw);
        lock (_lock)
        {
            try
            {
                if (!_dirEnsured)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                    _dirEnsured = true;
                }
                File.AppendAllText(_path, Format(sample) + "\n");
            }
            catch (Exception ex)
            {
                // History logging must never crash the app — but a write failure (e.g. disk full,
                // file locked) silently means this sample is lost forever, so it's worth a log line.
                AppLog.Error("BatteryHistoryService.Record", ex);
            }

            _window.Add(sample);
            TrimWindowToSpan();
        }
    }

    /// <summary>
    /// Loads the samples within <paramref name="window"/> (ending now) from the file into memory,
    /// replacing whatever slice was loaded before, and returns a snapshot oldest → newest. Prunes
    /// samples older than 14 days from the file on the first call.
    /// </summary>
    public static IReadOnlyList<BatterySample> LoadWindow(TimeSpan window)
    {
        lock (_lock)
        {
            _windowSpan = window;
            if (!_pruned) { PruneFile(); _pruned = true; }

            _window.Clear();
            var cutoff = DateTime.UtcNow - window;
            try
            {
                if (File.Exists(_path))
                    foreach (var line in File.ReadLines(_path))
                        if (TryParse(line, out var s) && s.AtUtc >= cutoff)
                            _window.Add(s);
            }
            catch (Exception ex)
            {
                AppLog.Error("BatteryHistoryService.LoadWindow", ex);
            }

            AppLog.Info($"History window loaded: span={window}, samples={_window.Count}");
            return [.. _window];
        }
    }

    /// <summary>A snapshot of the currently-loaded window (oldest → newest).</summary>
    public static IReadOnlyList<BatterySample> CurrentWindow()
    {
        lock (_lock) { return [.. _window]; }
    }

    // Samples are appended in time order, so an out-of-window prefix can be dropped from the front.
    private static void TrimWindowToSpan()
    {
        var cutoff = DateTime.UtcNow - _windowSpan;
        int drop = 0;
        while (drop < _window.Count && _window[drop].AtUtc < cutoff) drop++;
        if (drop > 0) _window.RemoveRange(0, drop);
    }

    private static void PruneFile()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var cutoff = DateTime.UtcNow - TimeSpan.FromDays(RetentionDays);
            var kept = new List<string>();
            int droppedCount = 0;
            foreach (var line in File.ReadLines(_path))
            {
                if (!TryParse(line, out var s)) continue;   // skip blank/corrupt lines
                if (s.AtUtc >= cutoff) kept.Add(line);
                else droppedCount++;
            }
            if (droppedCount > 0)
            {
                var tmp = _path + ".tmp";
                File.WriteAllLines(tmp, kept);
                File.Move(tmp, _path, overwrite: true);
                AppLog.Info($"History pruned: dropped {droppedCount} row(s) older than {RetentionDays}d, {kept.Count} kept.");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("BatteryHistoryService.PruneFile", ex);
        }
    }

    // CSV row: unixMillisUtc,soc,limit,powerMw  (limit column blank when Smart Charge is off).
    // Internal (not private) so unit tests can verify the round-trip without touching any file.
    internal static string Format(BatterySample s) => string.Create(CultureInfo.InvariantCulture,
        $"{new DateTimeOffset(s.AtUtc).ToUnixTimeMilliseconds()},{s.Soc},{s.LimitPct?.ToString(CultureInfo.InvariantCulture) ?? ""},{s.PowerMw}");

    internal static bool TryParse(string line, out BatterySample sample)
    {
        sample = default;
        var p = line.Split(',');
        if (p.Length < 4) return false;
        var ci = CultureInfo.InvariantCulture;
        if (!long.TryParse(p[0], NumberStyles.Integer, ci, out var ms))  return false;
        if (!int.TryParse (p[1], NumberStyles.Integer, ci, out var soc)) return false;
        int? limit = int.TryParse(p[2], NumberStyles.Integer, ci, out var l) ? l : null;
        if (!int.TryParse (p[3], NumberStyles.Integer, ci, out var pw))  return false;
        sample = new BatterySample(DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime, soc, limit, pw);
        return true;
    }
}
