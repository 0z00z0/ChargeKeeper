using System.Globalization;

namespace ChargeKeeper.Services;

/// <summary>One row of the performance log. A row carries either a processor reading or a resource
/// reading, never both, because the two are sampled at different rates.</summary>
internal readonly record struct PerformanceRow(
    DateTime AtUtc, double? ProcessorPercent,
    int? WorkingSetKb, int? PrivateBytesKb, int? Handles, int? Threads);

/// <summary>
/// File-backed self-measurement history, in its own file beside the two battery histories and
/// separate from the application log. Rows are buffered in memory and appended in one write per
/// second, because at the fast end of the rate range a write per sample would be ten file opens a
/// second — measurement loud enough to show up in what it is measuring.
/// </summary>
/// <remarks>
/// Retention is the battery history's mechanism, not a second one: the same
/// <see cref="CsvSampleStore"/>, the same shared <see cref="CsvSampleStore.Prune"/> with its temp
/// file, header re-emit and atomic move. What differs is the policy, and only because it has to.
/// The battery file is bounded by age alone because its sample interval is fixed at 20 s; this one's
/// rate is the user's to choose, and fourteen days at 10 Hz would be millions of rows, so the same
/// prune is also given a row cap. Pruning runs once per local day, as the battery file's does, and
/// additionally after enough appends to matter at the fast end.
/// </remarks>
internal static class PerformanceHistoryService
{
    /// <summary>Rows older than this are dropped, as the battery history drops its own.</summary>
    internal const int RetentionDays = 7;

    /// <summary>The ceiling age alone cannot supply here. Around 8 MB of rows.</summary>
    internal const int MaxRows = 200_000;

    /// <summary>Appends between prunes. At 10 Hz this is roughly half an hour, so the file cannot
    /// run far past <see cref="MaxRows"/> between the once-a-day passes.</summary>
    internal const int PruneAfterAppends = 20_000;

    /// <summary>How much of each series the live graph holds. One span for both series, so the two
    /// rates are visible as different point densities across the same stretch of time rather than as
    /// two plots of two different periods.</summary>
    internal static readonly TimeSpan WindowSpan = TimeSpan.FromMinutes(2);

    // A ceiling on the in-memory series independent of the span, so a clock jump forward cannot
    // leave an unbounded list behind. 2 minutes at 10 Hz is 1200 points; this is comfortably above.
    private const int MaxSeriesPoints = 4_000;

    // A buffer this size means a stalled flush cannot grow without bound. 10 Hz for a minute.
    private const int MaxPendingRows = 1_000;

    internal const string HeaderComment =
        "# ChargeKeeper performance history — one row per sample of the app measuring itself. " +
        "timestamp = ISO 8601 with local UTC offset; " +
        "cpu_percent = share of the whole machine over the interval ending at that timestamp, " +
        "sampled at the rate chosen on the App diagnostics page (blank on a resource row); " +
        "working_set_kb, private_kb, handles, threads = one process snapshot, " +
        "sampled once per second whatever that rate is (blank on a processor row).";
    internal const string HeaderColumns =
        "timestamp,cpu_percent,working_set_kb,private_kb,handles,threads";
    internal const string Header = HeaderComment + "\n" + HeaderColumns;

    private static readonly CsvSampleStore _store = new("performance-history.csv", Header);

    private static readonly Lock _lock = new();

    // Written but not yet on disk. Flushed from the once-a-second tick.
    private static readonly List<string> _pending = [];

    // The live window the graph draws, oldest to newest.
    private static readonly List<ProcessorReading> _processor = [];
    private static readonly List<ResourceReading>  _resources = [];

    private static DateTime? _prunedOnLocalDate;
    private static int _appendsSincePrune;

    public static string FilePath => _store.FilePath;

    /// <summary>Test-only seam: an isolated file, and a reset of state that is otherwise static and
    /// would leak from one test to the next. The same seam the two battery histories carry.</summary>
    internal static void UseTestPath(string path)
    {
        lock (_lock)
        {
            _store.UseTestPath(path);
            _pending.Clear();
            _processor.Clear();
            _resources.Clear();
            _prunedOnLocalDate = null;
            _appendsSincePrune = 0;
        }
    }

    /// <summary>Records one processor reading. Never throws.</summary>
    public static void Record(ProcessorReading reading)
    {
        lock (_lock)
        {
            _pending.Add(Format(reading));
            _processor.Add(reading);
            TrimLocked();
        }
    }

    /// <summary>Records one resource reading. Never throws.</summary>
    public static void Record(ResourceReading reading)
    {
        lock (_lock)
        {
            _pending.Add(Format(reading));
            _resources.Add(reading);
            TrimLocked();
        }
    }

    /// <summary>
    /// Writes everything buffered in one append, then prunes if it is time to. Never throws: a
    /// failed write loses those rows, which is worth a log line and nothing more — measurement must
    /// not be able to take the app down.
    /// </summary>
    public static void Flush()
    {
        lock (_lock)
        {
            try
            {
                if (_pending.Count > 0)
                {
                    _store.AppendLines(_pending);
                    _appendsSincePrune += _pending.Count;
                    _pending.Clear();
                }

                // Once per local day, as the battery history prunes, plus a count-based pass so the
                // fast end of the rate range cannot outrun a daily rewrite.
                var today = DateTime.Now.Date;
                if (_prunedOnLocalDate != today || _appendsSincePrune >= PruneAfterAppends)
                {
                    PruneLocked();
                    _prunedOnLocalDate = today;
                    _appendsSincePrune = 0;
                }
            }
            catch (Exception ex)
            {
                _pending.Clear();   // dropped rather than retried for ever
                AppLog.Error("PerformanceHistoryService.Flush", ex);
            }
        }
    }

    /// <summary>The live processor series, oldest to newest.</summary>
    public static IReadOnlyList<ProcessorReading> ProcessorWindow()
    {
        lock (_lock) { return [.. _processor]; }
    }

    /// <summary>The live resource series, oldest to newest.</summary>
    public static IReadOnlyList<ResourceReading> ResourceWindow()
    {
        lock (_lock) { return [.. _resources]; }
    }

    /// <summary>Drops both live series. Called when the feature is switched off, so switching it
    /// back on starts a fresh window rather than splicing across the gap.</summary>
    public static void ClearWindow()
    {
        lock (_lock) { _processor.Clear(); _resources.Clear(); }
    }

    /// <summary>Every row in the file, oldest first. For tests and for reading a session back; the
    /// graph draws from the in-memory window, never from disk.</summary>
    internal static IReadOnlyList<PerformanceRow> LoadAll()
    {
        lock (_lock)
        {
            var rows = new List<PerformanceRow>();
            try
            {
                foreach (var line in _store.ReadAllLines())
                    if (TryParse(line, out var row))
                        rows.Add(row);
            }
            catch (Exception ex)
            {
                AppLog.Error("PerformanceHistoryService.LoadAll", ex);
            }
            return rows;
        }
    }

    /// <summary>Applies retention now. Exposed so a session can prune on start and so the policy is
    /// testable without waiting for a flush to reach its threshold.</summary>
    internal static int Prune()
    {
        lock (_lock) return PruneLocked();
    }

    private static int PruneLocked()
    {
        try
        {
            var cutoff  = DateTime.UtcNow - TimeSpan.FromDays(RetentionDays);
            int dropped = _store.Prune(
                line => !TryParse(line, out var row) ? CsvRowVerdict.NotARow
                      : row.AtUtc >= cutoff          ? CsvRowVerdict.Keep
                                                     : CsvRowVerdict.Expired,
                maxRows: MaxRows);
            if (dropped > 0)
                AppLog.Info($"Performance history pruned: dropped {dropped} row(s).");
            return dropped;
        }
        catch (Exception ex)
        {
            AppLog.Error("PerformanceHistoryService.PruneLocked", ex);
            return 0;
        }
    }

    // Both series are appended in time order, so an out-of-window prefix comes off the front.
    private static void TrimLocked()
    {
        var cutoff = DateTime.UtcNow - WindowSpan;
        Trim(_processor, r => r.AtUtc, cutoff);
        Trim(_resources, r => r.AtUtc, cutoff);
        if (_pending.Count > MaxPendingRows) _pending.RemoveRange(0, _pending.Count - MaxPendingRows);

        static void Trim<T>(List<T> series, Func<T, DateTime> at, DateTime cutoff)
        {
            int drop = 0;
            while (drop < series.Count && at(series[drop]) < cutoff) drop++;
            if (drop > 0) series.RemoveRange(0, drop);
            if (series.Count > MaxSeriesPoints) series.RemoveRange(0, series.Count - MaxSeriesPoints);
        }
    }

    // Row: timestamp,cpu_percent,working_set_kb,private_kb,handles,threads. The timestamp is ISO
    // 8601 with the machine's local UTC offset, as both battery histories write it; the numbers are
    // InvariantCulture because this is a machine-readable file, not a display.
    internal static string Format(ProcessorReading r) => string.Create(CultureInfo.InvariantCulture,
        $"{Stamp(r.AtUtc)},{r.Percent:0.###},,,,");

    internal static string Format(ResourceReading r) => string.Create(CultureInfo.InvariantCulture,
        $"{Stamp(r.AtUtc)},,{r.WorkingSetKb},{r.PrivateBytesKb},{r.Handles},{r.Threads}");

    private static string Stamp(DateTime atUtc) => new DateTimeOffset(atUtc).ToLocalTime()
        .ToString("yyyy-MM-ddTHH:mm:ss.fffzzz", CultureInfo.InvariantCulture);

    /// <summary>Parses either kind of row. The header lines and anything corrupt fail here, which is
    /// how every reader skips them for free.</summary>
    internal static bool TryParse(string line, out PerformanceRow row)
    {
        row = default;
        var p = line.Split(',');
        if (p.Length < 6) return false;

        var ci = CultureInfo.InvariantCulture;
        if (!DateTimeOffset.TryParse(p[0], ci, DateTimeStyles.RoundtripKind, out var dto)) return false;

        double? cpu = double.TryParse(p[1], NumberStyles.Float, ci, out var c) ? c : null;
        int? working = int.TryParse(p[2], NumberStyles.Integer, ci, out var w) ? w : null;
        int? priv    = int.TryParse(p[3], NumberStyles.Integer, ci, out var v) ? v : null;
        int? handles = int.TryParse(p[4], NumberStyles.Integer, ci, out var h) ? h : null;
        int? threads = int.TryParse(p[5], NumberStyles.Integer, ci, out var t) ? t : null;

        // A row is one kind or the other. Neither present means a line that split into six fields
        // without being a row at all.
        if (cpu is null && working is null) return false;

        row = new PerformanceRow(dto.UtcDateTime, cpu, working, priv, handles, threads);
        return true;
    }
}

/// <summary>
/// Points the sampler at <see cref="PerformanceHistoryService"/>. A thin forwarder because the
/// service is static, like the two battery histories beside it, and a static class cannot itself
/// implement the interface.
/// </summary>
internal sealed class PerformanceHistorySink : IPerformanceSink
{
    public void Add(ProcessorReading reading) => PerformanceHistoryService.Record(reading);
    public void Add(ResourceReading reading)  => PerformanceHistoryService.Record(reading);
    public void Flush()                       => PerformanceHistoryService.Flush();
}
