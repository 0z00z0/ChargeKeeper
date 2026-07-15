using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

// BatteryHistoryService is a static class writing to a fixed AppData path in production; each
// test points it at an isolated temp file via UseTestPath (which also resets all in-memory state),
// so tests never touch the real user's battery-level-history.csv and can't see each other's data.
public class BatteryHistoryServiceTests : IDisposable
{
    private readonly string _testFile =
        Path.Combine(Path.GetTempPath(), $"lpt-history-test-{Guid.NewGuid():N}.csv");

    public BatteryHistoryServiceTests()
    {
        BatteryHistoryService.UseTestPath(_testFile);
        // Gap detection now reads the SHARED BatteryHistoryService.DowntimeThreshold (the user's
        // graph "Downtime gap threshold" setting), so pin it to the 1-minute default here — otherwise
        // these tests would depend on the dev machine's real settings.json.
        SettingsService.Current.DowntimeGapMinutes = 1;
    }

    public void Dispose()
    {
        try { File.Delete(_testFile); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void FormatThenParse_RoundTrips_WithLimit()
    {
        var sample = new BatterySample(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc), 75, 80, 4500);

        var line = BatteryHistoryService.Format(sample);
        Assert.True(BatteryHistoryService.TryParse(line, out var parsed));

        Assert.Equal(sample.Soc, parsed.Soc);
        Assert.Equal(sample.LimitPct, parsed.LimitPct);
        Assert.Equal(sample.PowerMw, parsed.PowerMw);
        Assert.Equal(sample.AtUtc, parsed.AtUtc, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void FormatThenParse_RoundTrips_WithNullLimit()
    {
        // Smart Charge off is recorded as a null limit — must round-trip as null, not 0.
        var sample = new BatterySample(DateTime.UtcNow, 42, null, -1200);

        var line = BatteryHistoryService.Format(sample);
        Assert.True(BatteryHistoryService.TryParse(line, out var parsed));

        Assert.Null(parsed.LimitPct);
        Assert.Equal(42, parsed.Soc);
        Assert.Equal(-1200, parsed.PowerMw);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not,enough")]
    [InlineData("abc,75,,4500")]     // non-numeric timestamp
    public void TryParse_RejectsMalformedLine(string line)
    {
        Assert.False(BatteryHistoryService.TryParse(line, out _));
    }

    [Fact]
    public void FormatThenParse_RoundTripsInstantToTheSecond_AcrossLocalOffset()
    {
        // A whole-second UTC instant must survive Format (which writes it in local time with the
        // machine's UTC offset) → TryParse (which converts back to UTC) unchanged, independent of the
        // dev machine's timezone — the offset in the file is human-readable sugar, not a new instant.
        var sample = new BatterySample(new DateTime(2026, 7, 15, 12, 30, 45, DateTimeKind.Utc), 63, 80, 2200);

        var line = BatteryHistoryService.Format(sample);
        Assert.True(BatteryHistoryService.TryParse(line, out var parsed));

        Assert.Equal(sample.AtUtc, parsed.AtUtc);              // same instant, to the second
        Assert.Equal(DateTimeKind.Utc, parsed.AtUtc.Kind);    // stored representation stays UTC
        // Serialized as ISO 8601 with a local offset, NOT a Unix-millis integer.
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}[+-]\d{2}:\d{2},", line);
    }

    [Fact]
    public void TryParse_SkipsHeaderLines()
    {
        // Both header lines (the '#' comment and the column-name row) must fail TryParse so the
        // existing skip-on-unparseable read loops drop them for free.
        Assert.False(BatteryHistoryService.TryParse(BatteryHistoryService.HeaderComment, out _));
        Assert.False(BatteryHistoryService.TryParse(BatteryHistoryService.HeaderColumns, out _));
    }

    [Fact]
    public void Record_OnFreshFile_WritesHeaderBlock_AndReadSkipsIt()
    {
        // The very first Record on a non-existent file writes the descriptive header block, then the
        // sample. On read, the header lines are skipped and only the real sample comes back.
        BatteryHistoryService.Record(60, 80, 3000);

        var lines = File.ReadAllLines(_testFile);
        Assert.Equal(BatteryHistoryService.HeaderComment, lines[0]);
        Assert.Equal(BatteryHistoryService.HeaderColumns, lines[1]);
        Assert.StartsWith("#", lines[0]);
        Assert.Equal(3, lines.Length);   // comment + columns + one data row

        var loaded = BatteryHistoryService.LoadWindow(TimeSpan.FromHours(1));
        var sample = Assert.Single(loaded);   // header skipped, only the sample
        Assert.Equal(60, sample.Soc);
    }

    [Fact]
    public void LoadWindow_Prune_PreservesHeaderBlock()
    {
        // A realistic file (header + one too-old + one kept row): the first LoadWindow prunes the
        // 20-day-old row and rewrites the file, and that rewrite must keep the header at the top.
        var tooOld = new BatterySample(DateTime.UtcNow.AddDays(-20), 10, null, 0);
        var kept   = new BatterySample(DateTime.UtcNow.AddDays(-1),  20, null, 0);
        File.WriteAllLines(_testFile,
        [
            BatteryHistoryService.HeaderComment,
            BatteryHistoryService.HeaderColumns,
            BatteryHistoryService.Format(tooOld),
            BatteryHistoryService.Format(kept),
        ]);

        BatteryHistoryService.LoadWindow(TimeSpan.FromDays(14));   // triggers the prune

        var lines = File.ReadAllLines(_testFile);
        Assert.Equal(BatteryHistoryService.HeaderComment, lines[0]);
        Assert.Equal(BatteryHistoryService.HeaderColumns, lines[1]);
        var remaining = Assert.Single(lines.Skip(2));
        Assert.True(BatteryHistoryService.TryParse(remaining, out var s));
        Assert.Equal(20, s.Soc);
    }

    [Fact]
    public void Record_ThenLoadWindow_ReturnsRecordedSample()
    {
        BatteryHistoryService.Record(60, 80, 3000);

        var loaded = BatteryHistoryService.LoadWindow(TimeSpan.FromHours(1));

        var sample = Assert.Single(loaded);
        Assert.Equal(60,   sample.Soc);
        Assert.Equal(80,   sample.LimitPct);
        Assert.Equal(3000, sample.PowerMw);
    }

    [Fact]
    public void LoadWindow_ExcludesSamplesOutsideRequestedSpan()
    {
        // Write an "old" sample directly (bypassing Record, which always timestamps "now") so its
        // age is under our control.
        var old = new BatterySample(DateTime.UtcNow.AddHours(-2), 50, null, 0);
        File.WriteAllText(_testFile, BatteryHistoryService.Format(old) + "\n");

        BatteryHistoryService.Record(90, null, 0);   // a fresh sample, "now"

        var loaded = BatteryHistoryService.LoadWindow(TimeSpan.FromHours(1));

        var sample = Assert.Single(loaded);
        Assert.Equal(90, sample.Soc);
    }

    [Fact]
    public void CurrentWindow_ReflectsLastLoadWindowCall()
    {
        BatteryHistoryService.Record(55, null, 0);
        BatteryHistoryService.LoadWindow(TimeSpan.FromHours(1));

        Assert.Single(BatteryHistoryService.CurrentWindow());
    }

    [Fact]
    public void CurrentSpan_MatchesLastLoadWindowArgument()
    {
        BatteryHistoryService.LoadWindow(TimeSpan.FromHours(6));

        Assert.Equal(TimeSpan.FromHours(6), BatteryHistoryService.CurrentSpan);
    }

    [Fact]
    public void LoadWindow_PrunesRowsOlderThan14DaysOnFirstCall()
    {
        var tooOld = new BatterySample(DateTime.UtcNow.AddDays(-20), 10, null, 0);
        var kept   = new BatterySample(DateTime.UtcNow.AddDays(-1),  20, null, 0);
        File.WriteAllLines(_testFile, [BatteryHistoryService.Format(tooOld), BatteryHistoryService.Format(kept)]);

        BatteryHistoryService.LoadWindow(TimeSpan.FromDays(14));   // first call → triggers the prune

        // The prune rewrites the file, so it re-emits the header block ahead of the surviving row(s).
        var lines = File.ReadAllLines(_testFile);
        Assert.Equal(BatteryHistoryService.HeaderComment, lines[0]);
        Assert.Equal(BatteryHistoryService.HeaderColumns, lines[1]);
        var rawLine = Assert.Single(lines.Skip(2));
        Assert.True(BatteryHistoryService.TryParse(rawLine, out var remaining));
        Assert.Equal(20, remaining.Soc);
    }

    // ── Downtime-gap detection (TODO #26) ───────────────────────────────────────

    [Fact]
    public void Record_FirstEverSample_ReportsNoGap()
    {
        // Nothing to compare against yet — must not report a spurious gap against a non-existent
        // "previous" sample.
        var gap = BatteryHistoryService.Record(80, null, 0);
        Assert.Null(gap);
    }

    [Fact]
    public void Record_ConsecutiveSamplesCloseTogether_ReportsNoGap()
    {
        var old = new BatterySample(DateTime.UtcNow.AddSeconds(-20), 80, null, 0);
        File.WriteAllText(_testFile, BatteryHistoryService.Format(old) + "\n");
        BatteryHistoryService.LoadWindow(TimeSpan.FromHours(1)); // load it into the in-memory window

        var gap = BatteryHistoryService.Record(79, null, 0); // normal ~20s tick later

        Assert.Null(gap);
    }

    [Fact]
    public void Record_AfterLongGap_ReportsDropAndDuration()
    {
        var beforeGap = DateTime.UtcNow.AddHours(-6);
        var old = new BatterySample(beforeGap, 90, null, 0);
        File.WriteAllText(_testFile, BatteryHistoryService.Format(old) + "\n");
        BatteryHistoryService.LoadWindow(TimeSpan.FromDays(1));

        var gap = BatteryHistoryService.Record(75, null, 0); // app just restarted after ~6h downtime

        Assert.NotNull(gap);
        Assert.Equal(15, gap!.Value.SocDropPercent); // 90% → 75%
        Assert.True(gap.Value.GapDuration >= TimeSpan.FromHours(5.9));
    }

    [Fact]
    public void Record_AfterGapWithRise_ReportsNegativeDrop()
    {
        // The battery kept charging (or was topped off) while the app wasn't running — a real,
        // legitimate reading the caller is expected to filter out as "not an anomaly", not
        // something this layer should hide or clamp away.
        var old = new BatterySample(DateTime.UtcNow.AddHours(-6), 60, null, 0);
        File.WriteAllText(_testFile, BatteryHistoryService.Format(old) + "\n");
        BatteryHistoryService.LoadWindow(TimeSpan.FromDays(1));

        var gap = BatteryHistoryService.Record(95, null, 0);

        Assert.NotNull(gap);
        Assert.True(gap!.Value.SocDropPercent < 0);
    }

    [Fact]
    public void Record_GapBelowUserDowntimeThreshold_ReportsNoGap()
    {
        // Unification guard: the anomaly gate now shares the graph's downtime threshold. Raising the
        // "Downtime gap threshold" so the graph would NOT draw a 6-minute hole as downtime must also
        // stop that hole producing gap info (hence no drain toast) — the disagreement #18 called out.
        SettingsService.Current.DowntimeGapMinutes = 30;   // graph collapses only gaps > 30 min
        var old = new BatterySample(DateTime.UtcNow.AddMinutes(-6), 80, null, 0);
        File.WriteAllText(_testFile, BatteryHistoryService.Format(old) + "\n");
        BatteryHistoryService.LoadWindow(TimeSpan.FromHours(1));

        var gap = BatteryHistoryService.Record(70, null, 0);   // 6 min later, a 10% drop

        Assert.Null(gap);   // below the 30-min downtime threshold → not reported
    }

    [Fact]
    public void Record_GapDetectionNone_StillReportsOvernightGap()
    {
        // Issue #40 decoupling: "None" (0) means "graph draws no breaks", NOT "stop watching for an
        // overnight battery drain". Across a genuine multi-hour hole the anomaly path must STILL
        // report the gap (it falls back to its own floor, DrainAnomalyPolicy.MinGap), so the safety
        // toast can still fire even though the graph has stopped drawing breaks.
        SettingsService.Current.DowntimeGapMinutes = 0;
        var old = new BatterySample(DateTime.UtcNow.AddHours(-8), 90, null, 0);
        File.WriteAllText(_testFile, BatteryHistoryService.Format(old) + "\n");
        BatteryHistoryService.LoadWindow(TimeSpan.FromDays(1));

        // Sanity: the graph gate really is disabled ("None" → MaxValue → no breaks drawn)…
        Assert.Equal(TimeSpan.MaxValue, BatteryHistoryService.DowntimeThreshold);
        // …but the anomaly gate falls back to the floor, so it keeps detecting.
        Assert.Equal(DrainAnomalyPolicy.MinGap, BatteryHistoryService.AnomalyGapThreshold);

        var gap = BatteryHistoryService.Record(60, null, 0);

        Assert.NotNull(gap);
        Assert.Equal(30, gap!.Value.SocDropPercent);          // 90 → 60
        Assert.True(gap.Value.GapDuration >= TimeSpan.FromHours(7.9));
    }

    [Fact]
    public void Record_GapDetectionNone_ReportsNoGapForShortHole()
    {
        // The "None" fallback is the anomaly floor (15 min), not zero — a brief hole shorter than the
        // floor is still not treated as downtime by the anomaly path (it could never clear
        // DrainAnomalyPolicy.ShouldWarn's own MinGap check anyway).
        SettingsService.Current.DowntimeGapMinutes = 0;
        var old = new BatterySample(DateTime.UtcNow.AddMinutes(-5), 80, null, 0);
        File.WriteAllText(_testFile, BatteryHistoryService.Format(old) + "\n");
        BatteryHistoryService.LoadWindow(TimeSpan.FromHours(1));

        var gap = BatteryHistoryService.Record(78, null, 0);   // 5 min later, below the 15-min floor

        Assert.Null(gap);
    }

    [Fact]
    public void Record_BeforeAnyLoadWindow_SeedsGapFromFileTail()
    {
        // No LoadWindow call → Record must lazily seed _lastPersisted by tail-reading the file, so a
        // gap against the last persisted sample is still detected on the very first Record.
        var beforeGap = new BatterySample(DateTime.UtcNow.AddHours(-7), 88, null, 0);
        File.WriteAllText(_testFile, BatteryHistoryService.Format(beforeGap) + "\n");

        var gap = BatteryHistoryService.Record(70, null, 0);

        Assert.NotNull(gap);
        Assert.Equal(18, gap!.Value.SocDropPercent);          // 88 → 70
        Assert.True(gap.Value.GapDuration >= TimeSpan.FromHours(6.9));
    }

    [Fact]
    public void Record_BeforeAnyLoadWindow_TailReadPicksTrueLastRow_InLargeFile()
    {
        // A file larger than the 8 KB tail window, so ReadLastSampleFromFile actually seeks the tail
        // (start > 0) and must drop its truncated first line yet still return the TRUE last row —
        // proving it reads the tail, not just the small-file whole-buffer path.
        var sb       = new System.Text.StringBuilder();
        var baseTime = DateTime.UtcNow.AddHours(-10);
        for (int i = 0; i < 600; i++)   // ~600 rows × ~30 bytes ≈ 18 KB > 8 KB window
            sb.Append(BatteryHistoryService.Format(new BatterySample(baseTime.AddSeconds(i), 50, null, 0)))
              .Append('\n');
        var last = new BatterySample(DateTime.UtcNow.AddHours(-9), 83, null, 0);
        sb.Append(BatteryHistoryService.Format(last)).Append('\n');
        File.WriteAllText(_testFile, sb.ToString());

        var gap = BatteryHistoryService.Record(60, null, 0);

        Assert.NotNull(gap);
        Assert.Equal(23, gap!.Value.SocDropPercent);          // 83 → 60, i.e. measured against the last row
    }

    [Fact]
    public void Record_AfterGapLongerThanLoadedWindow_StillReportsGap()
    {
        // Regression guard for the overnight-drain no-op: with only a 1h window loaded, a sample
        // from BEFORE an overnight (>1h) downtime falls OUTSIDE _window entirely. Gap detection must
        // compare against the last PERSISTED sample (from the file), not _window[^1], or the
        // overnight drain — the exact case the feature exists for — is never seen.
        var beforeGap = new BatterySample(DateTime.UtcNow.AddHours(-8), 90, null, 0);
        File.WriteAllText(_testFile, BatteryHistoryService.Format(beforeGap) + "\n");
        BatteryHistoryService.LoadWindow(TimeSpan.FromHours(1));   // the 8h-old sample is outside this window

        Assert.Empty(BatteryHistoryService.CurrentWindow());       // sanity: the window really is empty

        var gap = BatteryHistoryService.Record(75, null, 0);       // app "restarts" after 8h down

        Assert.NotNull(gap);
        Assert.Equal(15, gap!.Value.SocDropPercent);               // 90 → 75
        Assert.True(gap.Value.GapDuration >= TimeSpan.FromHours(7.9));
    }
}
