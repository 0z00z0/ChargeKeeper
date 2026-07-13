using System.Globalization;

namespace ChargeKeeper.Services;

/// <summary>
/// One recorded battery-capacity reading (TODO #24) — a single point in the slow-moving
/// degradation trend, NOT the fast SoC history. <see cref="FullChargeMwh"/> is the battery's
/// current maximum charge; <see cref="DesignMwh"/> is its as-new rated capacity, present only on
/// controllers that support <c>BatteryReport.DesignCapacityInMilliwattHours</c> — never
/// faked/guessed when absent.
/// </summary>
internal readonly record struct CapacitySample(DateTime AtUtc, int FullChargeMwh, int? DesignMwh);

/// <summary>
/// File-backed, slow-cadence battery capacity history (TODO #24) — tracks long-term degradation
/// (FullChargeCapacity trending down over months/years) completely separately from the fast SoC
/// history in <see cref="BatteryHistoryService"/>: capacity barely changes hour to hour, so logging
/// it at that ~20s cadence would be pure noise on disk for no benefit. One sample per calendar day
/// is plenty — the value of this data is in having many months of it, not fine time resolution, and
/// the sooner it starts, the sooner "capacity lost since new" becomes meaningful.
/// </summary>
internal static class BatteryCapacityHistoryService
{
    // All raw file I/O (path build, dir-ensure-once, append, read-last) lives in the shared
    // CsvSampleStore; this service keeps only its OWN domain logic (Format/TryParse and the
    // once-a-day cache below). Every store call happens under _lock, the same lock that guards that
    // cache — see CsvSampleStore's remarks on why it holds no lock of its own.
    private static readonly CsvSampleStore _store = new("capacity-history.csv");

    private static readonly Lock _lock = new();

    // Cached so a call on every battery event (which can fire many times an hour) doesn't re-open
    // and re-scan the file after the first successful write each day.
    private static DateTime? _lastRecordedDateLocal;

    /// <summary>Path to the CSV file — surfaced in UI/logs for backup/inspection if ever needed.</summary>
    public static string FilePath => _store.FilePath;

    /// <summary>
    /// TEST-ONLY seam: points the service at an isolated file and resets all in-memory state, same
    /// pattern as <see cref="BatteryHistoryService.UseTestPath"/> and for the same reason (every
    /// member here is static, so it otherwise persists for the whole process).
    /// </summary>
    internal static void UseTestPath(string path)
    {
        lock (_lock)
        {
            _store.UseTestPath(path);
            _lastRecordedDateLocal = null;
        }
    }

    /// <summary>
    /// Appends a sample only if none has been recorded yet today (LOCAL date — "once a day" should
    /// track the calendar day the user experiences, not a UTC one that rolls over mid-afternoon in
    /// most timezones). Cheap no-op on every call after the first success each day. Call from any
    /// battery-report event; never throws.
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
                // First check this process makes: also look at the FILE's last line, not just the
                // in-memory cache — a same-day app restart must not duplicate a row a previous
                // process already wrote before exiting. (ReadLastLine returns null when the file
                // doesn't exist yet, same as the old File.Exists guard.)
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
                _store.AppendLine(Format(sample));   // ensures the directory on first write
                _lastRecordedDateLocal = today;
            }
            catch (Exception ex)
            {
                // Logging must never crash the app — but a write failure silently means today's
                // sample is lost forever, so it's worth a log line.
                AppLog.Error("BatteryCapacityHistoryService.RecordIfNewDay", ex);
            }
        }
    }

    /// <summary>
    /// Loads every logged sample, oldest first. No windowing like <see cref="BatteryHistoryService"/>
    /// needs — at most one row per day, so even years of history stays trivially small to read
    /// whole.
    /// </summary>
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

    // CSV row: unixMillisUtc,fullChargeMwh,designMwh  (design column blank when unsupported).
    // Internal (not private) so unit tests can verify the round-trip without touching any file.
    internal static string Format(CapacitySample s) => string.Create(CultureInfo.InvariantCulture,
        $"{new DateTimeOffset(s.AtUtc).ToUnixTimeMilliseconds()},{s.FullChargeMwh},{s.DesignMwh?.ToString(CultureInfo.InvariantCulture) ?? ""}");

    internal static bool TryParse(string line, out CapacitySample sample)
    {
        sample = default;
        var p = line.Split(',');
        if (p.Length < 3) return false;
        var ci = CultureInfo.InvariantCulture;
        if (!long.TryParse(p[0], NumberStyles.Integer, ci, out var ms))   return false;
        if (!int.TryParse (p[1], NumberStyles.Integer, ci, out var full)) return false;
        int? design = int.TryParse(p[2], NumberStyles.Integer, ci, out var d) ? d : null;
        sample = new CapacitySample(DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime, full, design);
        return true;
    }
}
