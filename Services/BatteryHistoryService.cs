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
/// appended to <c>%AppData%\ChargeKeeper\battery-level-history.csv</c> with an ISO-8601 timestamp
/// (local UTC offset), so the graph survives restarts and downtime shows up as a gap in the
/// timeline. Data is kept for 14 days.
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
    /// The GRAPH's "is this hole in the timeline downtime?" threshold — the user's graph-gap setting
    /// (Settings → General → "Downtime gap threshold",
    /// <see cref="SettingsService.AppSettings.DowntimeGapMinutes"/>). The graph visually collapses any
    /// gap larger than this into a fixed-width break instead of a connecting line.
    /// <para>
    /// 0 ("None") → <see cref="TimeSpan.MaxValue"/>, so the graph draws NO breaks at all — NOT a
    /// literal zero-minute threshold. Read fresh each access so a settings change takes effect without
    /// a restart. The minimum positive setting (1 min) is three sample intervals, so a single late
    /// sample (scheduler jitter) is never drawn as downtime.
    /// </para>
    /// <para>
    /// This is a PRESENTATION knob only. The overnight-drain-anomaly gate does NOT read it directly —
    /// see <see cref="AnomalyGapThreshold"/> for why "None" must silence the graph's breaks without
    /// also disabling drain detection (issue #40).
    /// </para>
    /// </summary>
    public static TimeSpan DowntimeThreshold
    {
        get
        {
            // Read once: SettingsService.Current can be swapped between reads, so testing one value
            // and converting another could mix a "None" branch with a positive minute count.
            int minutes = SettingsService.Current.DowntimeGapMinutes;
            return minutes <= 0 ? TimeSpan.MaxValue : TimeSpan.FromMinutes(minutes);
        }
    }

    /// <summary>
    /// The gate <see cref="Record"/> uses to decide whether to REPORT a downtime gap to the
    /// drain-anomaly path — deliberately DECOUPLED from the graph's <see cref="DowntimeThreshold"/> on
    /// the "None" case (issue #40). Setting the graph gap to "None" means "stop drawing breaks", NOT
    /// "stop watching for an overnight battery drain": the safety warning must keep detecting.
    /// <para>
    /// Effective gate = <c>max(anomaly floor, user threshold when positive)</c>, falling back to the
    /// anomaly's own floor (<see cref="DrainAnomalyPolicy.MinGap"/>) when the user chose "None"
    /// (<see cref="TimeSpan.MaxValue"/>). So:
    /// <list type="bullet">
    /// <item>a positive user threshold still governs both graph and anomaly in agreement (raising it to
    /// 30 min stops a 6-min hole producing a toast, just as it stops the graph drawing it); but</item>
    /// <item>"None" leaves the anomaly floor in force, so a genuine multi-hour overnight gap is still
    /// reported even though the graph has stopped drawing breaks.</item>
    /// </list>
    /// The floor also means a sub-15-min hole is never handed to the anomaly path — it could never
    /// clear <see cref="DrainAnomalyPolicy.ShouldWarn"/>'s own <see cref="DrainAnomalyPolicy.MinGap"/>
    /// rate-trust check anyway, so gating here matches what the policy would accept. Drain detection
    /// still has its own explicit user off-switch (<c>DrainAnomalyWarningEnabled</c>), which is the
    /// intended way to silence it deliberately.
    /// </para>
    /// </summary>
    public static TimeSpan AnomalyGapThreshold
    {
        get
        {
            var userGate = DowntimeThreshold;
            // "None" (MaxValue) disables the GRAPH's breaks but must not disable drain detection:
            // fall back to the anomaly floor. Otherwise honour the user's threshold, never below it.
            if (userGate == TimeSpan.MaxValue) return DrainAnomalyPolicy.MinGap;
            return userGate > DrainAnomalyPolicy.MinGap ? userGate : DrainAnomalyPolicy.MinGap;
        }
    }

    // All raw file I/O (path build, dir-ensure-once, append, read-all, read-last) lives in the
    // shared CsvSampleStore; this service keeps only its OWN domain logic (Format/TryParse, the
    // windowing, pruning, and gap-detection state below). Every store call happens under _lock, the
    // same lock that guards that in-memory state — see CsvSampleStore's remarks on why it holds no
    // lock of its own.
    // Descriptive header (a leading '#' comment describing the file + units, then the column-name
    // row) written once when the store first creates the file, and re-emitted by PruneFile's rewrite.
    // Both lines fail TryParse, so LoadWindow/PruneFile skip them for free.
    internal const string HeaderComment =
        "# ChargeKeeper battery-level history — one row per ~20 s sample. " +
        "timestamp = ISO 8601 with local UTC offset; soc_percent = state of charge; " +
        "charge_limit_percent = Smart Charge limit (blank if off); " +
        "power_mw = charge power in milliwatts (negative = discharging).";
    internal const string HeaderColumns = "timestamp,soc_percent,charge_limit_percent,power_mw";
    internal const string Header = HeaderComment + "\n" + HeaderColumns;

    private static readonly CsvSampleStore _store = new("battery-level-history.csv", Header);

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
    /// gap info (TODO #26) when this sample landed more than <see cref="AnomalyGapThreshold"/> after
    /// the previous one — the anomaly gate, which tracks the graph's threshold while it is positive but
    /// keeps its own floor when the user picks "None" (issue #40). The caller (which owns the
    /// anomaly-rate threshold and toast-firing decision; this service stays a persistence layer, not a
    /// notification one) decides whether it was actually anomalous.
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
                // Gate on AnomalyGapThreshold, NOT the graph's DowntimeThreshold: the two agree while
                // the user threshold is positive, but "None" only stops the GRAPH drawing breaks — the
                // anomaly path falls back to its own floor so overnight-drain detection keeps running
                // (issue #40; see AnomalyGapThreshold remarks). The caller then applies its own
                // %/hour threshold before actually toasting.
                if (gap > AnomalyGapThreshold)
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
                // ReadExactly loops until the buffer is full; a single Stream.Read may return fewer
                // bytes than asked, and since we read FORWARD from `start` a short read would drop the
                // file's TAIL — exactly the newest rows this method exists to find.
                fs.ReadExactly(buffer);
                var text = System.Text.Encoding.UTF8.GetString(buffer);

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
                // BatteryHistoryService concern (the append-only store offers no rewrite op). Re-emit
                // the header block at the top: the kept rows are data-only (header lines fail TryParse,
                // so they were never added to `kept`), and without this the prune would drop the header.
                var path = _store.FilePath;
                var tmp = path + ".tmp";
                var output = new List<string>();
                if (_store.Header is { } h) output.AddRange(h.Split('\n'));
                output.AddRange(kept);
                File.WriteAllLines(tmp, output);
                File.Move(tmp, path, overwrite: true);
                AppLog.Info($"History pruned: dropped {droppedCount} row(s) older than {RetentionDays}d, {kept.Count} kept.");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("BatteryHistoryService.PruneFile", ex);
        }
    }

    // CSV row: timestamp,soc_percent,charge_limit_percent,power_mw where timestamp is ISO 8601 with
    // the machine's local UTC offset (e.g. 2026-07-15T14:30:00+02:00) and the limit column is blank
    // when Smart Charge is off. Internal (not private) so unit tests can verify the round-trip without
    // touching any file. The stored AtUtc is always Kind=Utc; the local offset in the file is purely
    // for human readability and round-trips the same instant.
    internal static string Format(BatterySample s) => string.Create(CultureInfo.InvariantCulture,
        $"{new DateTimeOffset(s.AtUtc).ToLocalTime().ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)},{s.Soc},{s.LimitPct?.ToString(CultureInfo.InvariantCulture) ?? ""},{s.PowerMw}");

    internal static bool TryParse(string line, out BatterySample sample)
    {
        sample = default;
        var p = line.Split(',');
        if (p.Length < 4) return false;
        var ci = CultureInfo.InvariantCulture;
        if (!DateTimeOffset.TryParse(p[0], ci, DateTimeStyles.RoundtripKind, out var dto)) return false;
        if (!int.TryParse (p[1], NumberStyles.Integer, ci, out var soc)) return false;
        int? limit = int.TryParse(p[2], NumberStyles.Integer, ci, out var l) ? l : null;
        if (!int.TryParse (p[3], NumberStyles.Integer, ci, out var pw))  return false;
        sample = new BatterySample(dto.UtcDateTime, soc, limit, pw);
        return true;
    }
}
