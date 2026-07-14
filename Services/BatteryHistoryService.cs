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

    /// <summary>
    /// The single "is this hole in the timeline downtime?" threshold, read by BOTH the graph (which
    /// visually collapses any gap this size into a fixed-width break) AND <see cref="Record"/>'s
    /// anomaly gate below — so a user's graph-gap setting (Settings → General → "Downtime gap
    /// threshold", <see cref="SettingsService.AppSettings.DowntimeGapMinutes"/>) can no longer
    /// disagree with when an overnight-drain toast may fire. Previously the graph read the setting
    /// while the anomaly path used a separate fixed 3-sample-interval constant, so raising the graph
    /// gap to (say) 30 min or "None" still let a drain toast fire for a gap the graph refused to draw.
    /// <para>
    /// 0 ("None") → <see cref="TimeSpan.MaxValue"/> (gap detection disabled everywhere), NOT a literal
    /// zero-minute threshold. Read fresh each access so a settings change takes effect without a
    /// restart. The minimum positive setting (1 min) is three sample intervals, so a single late
    /// sample (scheduler jitter) is never treated as downtime — the old constant's purpose, now
    /// inherent in the setting's floor. The drain-anomaly path applies a further rate-trust floor and
    /// %/hour threshold (<see cref="DrainAnomalyPolicy"/>) on top of this gate before toasting.
    /// </para>
    /// </summary>
    public static TimeSpan DowntimeThreshold =>
        SettingsService.Current.DowntimeGapMinutes <= 0
            ? TimeSpan.MaxValue
            : TimeSpan.FromMinutes(SettingsService.Current.DowntimeGapMinutes);

    // All raw file I/O (path build, dir-ensure-once, append, read-all, read-last) lives in the
    // shared CsvSampleStore; this service keeps only its OWN domain logic (Format/TryParse, the
    // windowing, pruning, and gap-detection state below). Every store call happens under _lock, the
    // same lock that guards that in-memory state — see CsvSampleStore's remarks on why it holds no
    // lock of its own.
    private static readonly CsvSampleStore _store = new("history.csv");

    private static readonly Lock _lock = new();

    // The slice currently loaded for the dashboard, oldest → newest.
    private static readonly List<BatterySample> _window = [];
    private static TimeSpan _windowSpan = TimeSpan.FromHours(1);
    private static bool _pruned;

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
    public static string FilePath => _store.FilePath;

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
            _store.UseTestPath(path);
            _window.Clear();
            _windowSpan = TimeSpan.FromHours(1);
            _pruned = false;
            _lastPersisted = null;
            _lastPersistedLoaded = false;
        }
    }

    /// <summary>
    /// Appends a sample to the file and to the in-memory window. Thread-safe; never throws. Returns
    /// gap info (TODO #26) when this sample landed more than <see cref="DowntimeThreshold"/> (the same
    /// gate the graph uses) after the previous one — the caller (which owns the anomaly-rate threshold
    /// and toast-firing decision; this service stays a persistence layer, not a notification one)
    /// decides whether it was actually anomalous.
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
                // Gate on the SHARED DowntimeThreshold so the anomaly path and the graph agree on
                // what counts as downtime (see the field remarks). The caller then applies its own
                // rate-trust floor + %/hour threshold before actually toasting.
                if (gap > DowntimeThreshold)
                    gapInfo = new DowntimeGapInfo(previous.Soc - sample.Soc, gap);
            }

            try
            {
                _store.AppendLine(Format(sample));
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
                foreach (var line in _store.ReadAllLines())
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
    /// <para>
    /// Reads only the file's TAIL rather than parsing every row: a row is a few tens of bytes, so an
    /// 8 KB window from the end holds hundreds of them, and this runs under the Record lock — an
    /// up-to-60k-row full scan there would stall an incoming sample. The window widens and retries in
    /// the pathological case where the tail is all blank/corrupt. Opens the file directly (as
    /// <see cref="PruneFile"/> already does) since the append-only store offers no tail-seek.
    /// </para>
    /// </summary>
    private static BatterySample? ReadLastSampleFromFile()
    {
        try
        {
            var path = _store.FilePath;
            if (!File.Exists(path)) return null;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long length = fs.Length;
            if (length == 0) return null;

            for (long window = 8192; ; window *= 8)
            {
                long start = Math.Max(0, length - window);
                fs.Seek(start, SeekOrigin.Begin);
                var buffer = new byte[length - start];
                int read = fs.Read(buffer, 0, buffer.Length);
                var text = System.Text.Encoding.UTF8.GetString(buffer, 0, read);

                // Unless we started at byte 0, the first line in the window is very likely truncated
                // mid-row — skip it so only whole rows are parsed. Rows are ASCII, so a split across a
                // multi-byte char could only land in that dropped first line anyway.
                var lines = text.Split('\n');
                int firstComplete = start == 0 ? 0 : 1;
                for (int i = lines.Length - 1; i >= firstComplete; i--)
                    if (TryParse(lines[i], out var s))
                        return s;

                if (start == 0) return null;   // whole file scanned, nothing parseable
            }
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
            var cutoff = DateTime.UtcNow - TimeSpan.FromDays(RetentionDays);
            var kept = new List<string>();
            int droppedCount = 0;
            foreach (var line in _store.ReadAllLines())   // empty when the file doesn't exist yet
            {
                if (!TryParse(line, out var s)) continue;   // skip blank/corrupt lines
                if (s.AtUtc >= cutoff) kept.Add(line);
                else droppedCount++;
            }
            if (droppedCount > 0)
            {
                // Rewrite in place via a temp file + atomic move — a whole-file rewrite that stays a
                // BatteryHistoryService concern (the append-only store offers no rewrite op).
                var path = _store.FilePath;
                var tmp = path + ".tmp";
                File.WriteAllLines(tmp, kept);
                File.Move(tmp, path, overwrite: true);
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
