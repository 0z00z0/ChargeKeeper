using System.Globalization;

namespace ChargeKeeper.Services;

/// <summary>One recorded battery reading. Power is stored in milliwatts (positive = charging).</summary>
internal readonly record struct BatterySample(DateTime AtUtc, int Soc, int? LimitPct, int PowerMw);

/// <summary>
/// Reported by <see cref="BatteryHistoryService.Record"/> when a sample lands after a gap large
/// enough to plausibly be downtime (TODO #26). <see cref="SocDropPercent"/> can be negative (SoC
/// rose across the gap, e.g. it kept charging while the app wasn't running) — the caller filters
/// that out along with anything below its own anomaly-rate threshold before ever toasting.
/// </summary>
internal readonly record struct DowntimeGapInfo(int SocDropPercent, TimeSpan GapDuration);

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

    // A gap must span at least this many missed ticks before Record() reports it as possible
    // downtime at all — a single slightly-late sample (scheduler jitter) isn't "downtime". This is
    // independent of the user-configurable DowntimeGapMinutes setting, which only controls how the
    // GRAPH visually collapses a gap; this is the much lower bar for "did the app plausibly not run
    // continuously", which the caller's own drop%-per-hour anomaly threshold (TODO #26, default
    // ~3%/hour) further filters before ever toasting.
    private static readonly TimeSpan MinGapForAnomalyCheck = TimeSpan.FromSeconds(SampleIntervalSeconds * 3);

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

    // Last sample actually persisted to the file, tracked independently of the (span-limited)
    // _window so downtime-gap detection (TODO #26) still works when the gap is LONGER than the
    // loaded window — the overnight case, where LoadWindow(1h) leaves the pre-downtime sample
    // outside _window entirely (so comparing against _window[^1] would see "no previous" and never
    // report the gap). Seeded from the newest row in the file (by LoadWindow, or lazily on the first
    // Record if LoadWindow never ran) so a just-restarted process compares this sample against the
    // pre-downtime one.
    private static BatterySample? _lastPersisted;
    private static bool _lastPersistedLoaded;

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
            _lastPersisted = null;
            _lastPersistedLoaded = false;
        }
    }

    /// <summary>
    /// Appends a sample to the file and to the in-memory window. Thread-safe; never throws. Returns
    /// gap info (TODO #26) when this sample landed more than <see cref="MinGapForAnomalyCheck"/>
    /// after the previous one — the caller (which owns the anomaly-rate threshold and toast-firing
    /// decision; this service stays a persistence layer, not a notification one) decides whether it
    /// was actually anomalous.
    /// </summary>
    public static DowntimeGapInfo? Record(int soc, int? limitPct, int powerMw)
    {
        var sample = new BatterySample(DateTime.UtcNow, soc, limitPct, powerMw);
        DowntimeGapInfo? gapInfo = null;

        lock (_lock)
        {
            // Compare against the last PERSISTED sample, not _window[^1] (which is trimmed to the
            // loaded span) — see the _lastPersisted field comment for why the windowed compare
            // missed the overnight case. Lazy fallback seed for when Record runs before any
            // LoadWindow (LoadWindow normally seeds it during its own single file read).
            if (!_lastPersistedLoaded)
            {
                _lastPersisted = ReadLastSampleFromFile();
                _lastPersistedLoaded = true;
            }
            if (_lastPersisted is { } previous)
            {
                var gap = sample.AtUtc - previous.AtUtc;
                if (gap > MinGapForAnomalyCheck)
                    gapInfo = new DowntimeGapInfo(previous.Soc - sample.Soc, gap);
            }

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
            _lastPersisted = sample;
        }

        return gapInfo;
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
                        if (TryParse(line, out var s))
                        {
                            // Seed gap-detection's last-persisted sample from the newest row overall
                            // (rows are appended in time order) — NOT just rows inside the window, or
                            // an overnight gap (where every row is older than the cutoff) would leave
                            // it unseeded. Piggybacks on this single file read instead of Record
                            // re-reading the file itself.
                            _lastPersisted = s;
                            if (s.AtUtc >= cutoff) _window.Add(s);
                        }
                _lastPersistedLoaded = true;
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

    /// <summary>
    /// Reads the newest parseable sample from the file (rows are appended in time order, so it's the
    /// last valid line), or null if the file is missing/empty/all-corrupt. Only used as the seed for
    /// gap detection (<see cref="_lastPersisted"/>) when <see cref="Record"/> runs before any
    /// <see cref="LoadWindow"/> — the normal startup path seeds it from LoadWindow's own file read.
    /// </summary>
    private static BatterySample? ReadLastSampleFromFile()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            BatterySample? last = null;
            foreach (var line in File.ReadLines(_path))
                if (TryParse(line, out var s))
                    last = s;
            return last;
        }
        catch (Exception ex)
        {
            AppLog.Error("BatteryHistoryService.ReadLastSampleFromFile", ex);
            return null;
        }
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
