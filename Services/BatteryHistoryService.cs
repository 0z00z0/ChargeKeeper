using System.Globalization;
using ChargeKeeper.Helpers;

namespace ChargeKeeper.Services;

/// <summary>One recorded battery reading. Power is stored in milliwatts (positive = charging).
/// <paramref name="State"/> is null for rows written before the state was recorded: the sign of
/// <paramref name="PowerMw"/> cannot tell mains-with-no-flow from battery-with-no-drain, so an
/// unrecorded state stays unknown rather than being guessed at.</summary>
internal readonly record struct BatterySample(
    DateTime AtUtc, int Soc, int? LimitPct, int PowerMw, PowerState? State = null);

/// <summary>
/// Reported when a sample lands after a gap large enough to plausibly be downtime.
/// <see cref="SocDropPercent"/> can be negative when SoC rose across the gap; the caller filters that
/// out along with anything below its own anomaly-rate threshold.
/// </summary>
internal readonly record struct DowntimeGapInfo(int SocDropPercent, TimeSpan GapDuration);

/// <summary>
/// File-backed battery history. Every sample (SoC %, Smart-Charge limit %, charge power mW) is
/// appended to <c>%AppData%\ChargeKeeper\battery-level-history.csv</c> with an ISO-8601 timestamp, so
/// the graph survives restarts and downtime shows up as a gap. Rows are kept for 14 days, and only
/// the currently-selected time window is held in memory.
/// </summary>
internal static class BatteryHistoryService
{
    private const int RetentionDays = 14;

    /// <summary>Must match the period App's history-sampling timer runs on; the dashboard's
    /// gap-detection threshold derives from it.</summary>
    public const int SampleIntervalSeconds = 20;

    /// <summary>
    /// The graph's downtime threshold, from Settings → General; larger gaps are drawn as a break. A
    /// setting of 0 means "None" and maps to <see cref="TimeSpan.MaxValue"/>, drawing no breaks at
    /// all — not a zero-minute threshold. Presentation only; see <see cref="AnomalyGapThreshold"/>.
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
    /// Decoupled from <see cref="DowntimeThreshold"/> on the "None" case: that means "stop drawing
    /// breaks", not "stop watching for an overnight drain". A positive user threshold governs both;
    /// "None" leaves <see cref="DrainAnomalyPolicy.MinGap"/> in force. Drain detection has its own
    /// off-switch in <c>DrainAnomalyWarningEnabled</c>.
    /// </summary>
    public static TimeSpan AnomalyGapThreshold
    {
        get
        {
            var userGate = DowntimeThreshold;
            if (userGate == TimeSpan.MaxValue) return DrainAnomalyPolicy.MinGap;
            return userGate > DrainAnomalyPolicy.MinGap ? userGate : DrainAnomalyPolicy.MinGap;
        }
    }

    // Raw file I/O lives in the shared CsvSampleStore; every call to it happens under _lock, the same
    // lock that guards the in-memory state below. The header is written once when the store creates
    // the file and re-emitted by PruneFile's rewrite; both its lines fail TryParse, so readers skip
    // them for free.
    internal const string HeaderComment =
        "# ChargeKeeper battery-level history — one row per ~20 s sample. " +
        "timestamp = ISO 8601 with local UTC offset; soc_percent = state of charge; " +
        "charge_limit_percent = Smart Charge limit (blank if off); " +
        "power_mw = charge power in milliwatts (negative = discharging); " +
        "power_state = Discharging, Charging or IdleOnMains (blank in rows written before it was recorded).";
    internal const string HeaderColumns = "timestamp,soc_percent,charge_limit_percent,power_mw,power_state";
    internal const string Header = HeaderComment + "\n" + HeaderColumns;

    private static readonly CsvSampleStore _store = new("battery-level-history.csv", Header);

    private static readonly Lock _lock = new();

    // The slice currently loaded for the dashboard, oldest → newest.
    private static readonly List<BatterySample> _window = [];
    private static TimeSpan _windowSpan = TimeSpan.FromHours(1);

    // Local date the file was last pruned on, so a process running for weeks keeps pruning rather
    // than holding every row it ever wrote.
    private static DateTime? _prunedOnLocalDate;

    // Last sample persisted to the file, tracked independently of the span-limited _window so gap
    // detection still works when the gap is LONGER than the loaded window — the overnight case,
    // where every row falls outside a 1 h window and comparing against _window[^1] would see no
    // previous sample at all.
    private static BatterySample? _lastPersisted;
    private static bool _lastPersistedLoaded;

    public static string FilePath => _store.FilePath;

    /// <summary>The span the last <see cref="LoadWindow"/> call loaded.</summary>
    public static TimeSpan CurrentSpan { get { lock (_lock) return _windowSpan; } }

    /// <summary>Test-only seam: an isolated file, and a reset of state that is otherwise static and
    /// would leak from one test to the next.</summary>
    internal static void UseTestPath(string path)
    {
        lock (_lock)
        {
            _store.UseTestPath(path);
            _window.Clear();
            _windowSpan = TimeSpan.FromHours(1);
            _prunedOnLocalDate = null;
            _lastPersisted = null;
            _lastPersistedLoaded = false;
        }
    }

    /// <summary>
    /// Thread-safe; never throws. Returns gap info when the sample landed more than
    /// <see cref="AnomalyGapThreshold"/> after the previous one; the caller owns the anomaly-rate
    /// threshold and the decision to warn.
    /// </summary>
    public static DowntimeGapInfo? Record(int soc, int? limitPct, int powerMw, PowerState? state = null)
    {
        var sample = new BatterySample(DateTime.UtcNow, soc, limitPct, powerMw, state);
        DowntimeGapInfo? gapInfo = null;

        lock (_lock)
        {
            // Lazy seed for when Record runs before any LoadWindow; LoadWindow normally seeds this
            // during its own single file read.
            if (!_lastPersistedLoaded)
            {
                _lastPersisted = ReadLastSampleFromFile();
                _lastPersistedLoaded = true;
            }
            if (_lastPersisted is { } previous)
            {
                var gap = sample.AtUtc - previous.AtUtc;
                // AnomalyGapThreshold, not the graph's DowntimeThreshold — see its remarks.
                if (gap > AnomalyGapThreshold)
                    gapInfo = new DowntimeGapInfo(previous.Soc - sample.Soc, gap);
            }

            try
            {
                _store.AppendLine(Format(sample));
            }
            catch (Exception ex)
            {
                // History logging must never crash the app, but a failed write loses this sample for
                // good, so it is worth a log line.
                AppLog.Error("BatteryHistoryService.Record", ex);
            }

            _window.Add(sample);
            TrimWindowToSpan();
            _lastPersisted = sample;
        }

        return gapInfo;
    }

    /// <summary>
    /// Loads the samples within <paramref name="window"/> (ending now), replacing whatever slice was
    /// loaded before, and returns a snapshot oldest → newest. Prunes once per local day.
    /// </summary>
    public static IReadOnlyList<BatterySample> LoadWindow(TimeSpan window)
    {
        lock (_lock)
        {
            _windowSpan = window;
            // Once per local day, not once per process: this app runs for weeks at a time, and every
            // time-scale click re-reads the whole file.
            var today = DateTime.Now.Date;
            if (_prunedOnLocalDate != today) { PruneFile(); _prunedOnLocalDate = today; }

            _window.Clear();
            var cutoff = DateTime.UtcNow - window;
            try
            {
                foreach (var line in _store.ReadAllLines())
                    if (TryParse(line, out var s))
                    {
                        // Seeded from the newest row overall, not just rows inside the window: an
                        // overnight gap puts every row before the cutoff.
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
    /// The newest parseable sample, or null when the file is missing, empty or all corrupt. Reads the
    /// tail rather than every row, widening the window on retry, because this runs under the
    /// <see cref="Record"/> lock where a 60k-row scan would stall an incoming sample.
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
                // ReadExactly, not Read: a short read here would drop the file's tail, which is
                // exactly the newest rows this method exists to find.
                fs.ReadExactly(buffer);
                var text = System.Text.Encoding.UTF8.GetString(buffer);

                // Unless the window starts at byte 0 its first line is probably truncated mid-row, so
                // skip it. Rows are ASCII, so a multi-byte split could only land there anyway.
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
                // Temp file plus atomic move. The header is re-emitted because header lines fail
                // TryParse and so never reach `kept`.
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

    // CSV row: timestamp,soc_percent,charge_limit_percent,power_mw,power_state. The timestamp is ISO
    // 8601 with the machine's local UTC offset (AtUtc itself is always Kind=Utc; the offset is for
    // readability and round-trips the same instant), the limit column is blank when Smart Charge is
    // off, and the state column is the PowerState member name, blank when it is unknown.
    internal static string Format(BatterySample s) => string.Create(CultureInfo.InvariantCulture,
        $"{new DateTimeOffset(s.AtUtc).ToLocalTime().ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)},{s.Soc},{s.LimitPct?.ToString(CultureInfo.InvariantCulture) ?? ""},{s.PowerMw},{s.State?.ToString() ?? ""}");

    internal static bool TryParse(string line, out BatterySample sample)
    {
        sample = default;
        var p = line.Split(',');
        // Four columns still parse: every row written before the state column carries only those.
        if (p.Length < 4) return false;
        var ci = CultureInfo.InvariantCulture;
        if (!DateTimeOffset.TryParse(p[0], ci, DateTimeStyles.RoundtripKind, out var dto)) return false;
        if (!int.TryParse (p[1], NumberStyles.Integer, ci, out var soc)) return false;
        int? limit = int.TryParse(p[2], NumberStyles.Integer, ci, out var l) ? l : null;
        if (!int.TryParse (p[3], NumberStyles.Integer, ci, out var pw))  return false;
        // Enum.TryParse also accepts integer text, so IsDefined is what rejects a number outside the
        // enum rather than storing a state that does not exist.
        PowerState? state = p.Length > 4
                         && Enum.TryParse<PowerState>(p[4], ignoreCase: true, out var st)
                         && Enum.IsDefined(st) ? st : null;
        sample = new BatterySample(dto.UtcDateTime, soc, limit, pw, state);
        return true;
    }
}
