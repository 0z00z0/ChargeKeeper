using System.Globalization;

namespace ChargeKeeper.Services;

/// <summary>
/// A point in the slow degradation trend, not the fast SoC history. <see cref="DesignMwh"/> is the
/// as-new rated capacity, present only on controllers that report it and never guessed when absent.
/// </summary>
internal readonly record struct CapacitySample(DateTime AtUtc, int FullChargeMwh, int? DesignMwh);

/// <summary>
/// File-backed capacity history, tracking long-term degradation separately from the fast SoC history
/// in <see cref="BatteryHistoryService"/>. Capacity barely changes hour to hour, so one sample per
/// calendar day is enough; the value is in having months of it.
/// </summary>
internal static class BatteryCapacityHistoryService
{
    // Raw file I/O lives in the shared CsvSampleStore; every call to it happens under _lock, the same
    // lock that guards the once-a-day cache below. The header is written when the store creates the
    // file; both its lines fail TryParse, so readers skip them for free. Nothing here rewrites the
    // file, so there is no prune path to re-emit it (unlike BatteryHistoryService).
    internal const string HeaderComment =
        "# ChargeKeeper battery-capacity history — one row per calendar day, kept indefinitely. " +
        "timestamp = ISO 8601 with local UTC offset; " +
        "full_charge_mwh = current full-charge capacity; " +
        "design_capacity_mwh = as-new rated capacity in milliwatt-hours (blank if the controller doesn't report it).";
    internal const string HeaderColumns = "timestamp,full_charge_mwh,design_capacity_mwh";
    internal const string Header = HeaderComment + "\n" + HeaderColumns;

    private static readonly CsvSampleStore _store = new("battery-capacity-history.csv", Header);

    private static readonly Lock _lock = new();

    // Cached so a call on every battery event, which can fire many times an hour, doesn't re-open and
    // re-scan the file after the first successful write each day.
    private static DateTime? _lastRecordedDateLocal;

    public static string FilePath => _store.FilePath;

    /// <summary>Test-only seam: an isolated file, and a reset of state that is otherwise static and
    /// would leak from one test to the next.</summary>
    internal static void UseTestPath(string path)
    {
        lock (_lock)
        {
            _store.UseTestPath(path);
            _lastRecordedDateLocal = null;
        }
    }

    /// <summary>
    /// Appends a sample only if none has been recorded yet today. The date is the LOCAL one, so this
    /// tracks the calendar day the user experiences rather than a UTC one that rolls over
    /// mid-afternoon. Safe to call from any battery-report event; never throws.
    /// </summary>
    public static void RecordIfNewDay(int fullChargeMwh, int? designMwh)
    {
        if (fullChargeMwh <= 0) return;   // not a real reading — never log garbage
        var today = DateTime.Now.Date;

        lock (_lock)
        {
            if (_lastRecordedDateLocal == today) return;

            try
            {
                // On this process's first check, consult the file too: a same-day restart must not
                // duplicate a row a previous process already wrote.
                if (_lastRecordedDateLocal is null)
                {
                    var lastLine = _store.ReadLastLine();
                    if (lastLine is not null && TryParse(lastLine, out var last) &&
                        last.AtUtc.ToLocalTime().Date == today)
                    {
                        _lastRecordedDateLocal = today;
                        return;
                    }
                }

                var sample = new CapacitySample(DateTime.UtcNow, fullChargeMwh, designMwh);
                _store.AppendLine(Format(sample));
                _lastRecordedDateLocal = today;
            }
            catch (Exception ex)
            {
                // Logging must never crash the app, but a failed write loses today's sample for
                // good, so it is worth a log line.
                AppLog.Error("BatteryCapacityHistoryService.RecordIfNewDay", ex);
            }
        }
    }

    /// <summary>Loads every sample, oldest first. No windowing: at most one row per day, so years of
    /// history stays small enough to read whole.</summary>
    public static IReadOnlyList<CapacitySample> LoadAll()
    {
        lock (_lock)
        {
            var result = new List<CapacitySample>();
            try
            {
                foreach (var line in _store.ReadAllLines())
                    if (TryParse(line, out var s))
                        result.Add(s);
            }
            catch (Exception ex)
            {
                AppLog.Error("BatteryCapacityHistoryService.LoadAll", ex);
            }
            return result;
        }
    }

    // CSV row: timestamp,full_charge_mwh,design_capacity_mwh. The timestamp is ISO 8601 with the
    // machine's local UTC offset (AtUtc itself is always Kind=Utc; the offset is for readability and
    // round-trips the same instant), and the design column is blank when the controller is silent.
    internal static string Format(CapacitySample s) => string.Create(CultureInfo.InvariantCulture,
        $"{new DateTimeOffset(s.AtUtc).ToLocalTime().ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture)},{s.FullChargeMwh},{s.DesignMwh?.ToString(CultureInfo.InvariantCulture) ?? ""}");

    internal static bool TryParse(string line, out CapacitySample sample)
    {
        sample = default;
        var p = line.Split(',');
        if (p.Length < 3) return false;
        var ci = CultureInfo.InvariantCulture;
        if (!DateTimeOffset.TryParse(p[0], ci, DateTimeStyles.RoundtripKind, out var dto)) return false;
        if (!int.TryParse (p[1], NumberStyles.Integer, ci, out var full)) return false;
        int? design = int.TryParse(p[2], NumberStyles.Integer, ci, out var d) ? d : null;
        sample = new CapacitySample(dto.UtcDateTime, full, design);
        return true;
    }
}
