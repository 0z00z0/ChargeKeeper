using ChargeKeeper.Helpers;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

// BatteryHistoryService is static and writes to a fixed AppData path, so each test points it at an
// isolated temp file via UseTestPath, which also resets the in-memory state.
public class BatteryHistoryServiceTests : IDisposable
{
    private readonly string _testFile =
        Path.Combine(Path.GetTempPath(), $"lpt-history-test-{Guid.NewGuid():N}.csv");

    public BatteryHistoryServiceTests()
    {
        BatteryHistoryService.UseTestPath(_testFile);
        // Gap detection reads the graph's "Downtime gap threshold" setting, so pin it here rather
        // than depend on the dev machine's settings.json.
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
    public void FormatThenParse_RoundTripsTheRecordedPowerState()
    {
        foreach (var state in Enum.GetValues<PowerState>())
        {
            var sample = new BatterySample(new DateTime(2026, 3, 4, 9, 0, 0, DateTimeKind.Utc), 55, null, 0, state);
            var line   = BatteryHistoryService.Format(sample);

            // The state column by position: temperature_c trails it since the thermal reading was
            // added, so "the last column" no longer names it.
            Assert.Equal($"{state}", line.Split(',')[4]);
            Assert.True(BatteryHistoryService.TryParse(line, out var parsed));
            Assert.Equal(state, parsed.State);
        }
    }

    [Fact]
    public void FormatThenParse_LeavesAnUnrecordedStateNull()
    {
        // The column is written empty rather than defaulted to a state: the sign of power_mw cannot
        // separate mains-with-no-flow from battery-with-no-drain, so nothing is guessed.
        var line = BatteryHistoryService.Format(new BatterySample(DateTime.UtcNow, 55, null, 0));

        Assert.EndsWith(",", line);
        Assert.True(BatteryHistoryService.TryParse(line, out var parsed));
        Assert.Null(parsed.State);
    }

    [Theory]
    [InlineData("2026-01-01T12:00:00+00:00,75,80,4500")]           // four columns — written before the state
    [InlineData("2026-01-01T12:00:00+00:00,75,80,4500,")]          // the column present and empty
    [InlineData("2026-01-01T12:00:00+00:00,75,80,4500,9")]         // a number outside the enum
    [InlineData("2026-01-01T12:00:00+00:00,75,80,4500,Sunshine")]  // a name that is not a state
    public void TryParse_LeavesAnUnreadableStateNull_WithoutLosingTheRow(string line)
    {
        Assert.True(BatteryHistoryService.TryParse(line, out var parsed));
        Assert.Equal(75, parsed.Soc);
        Assert.Equal(4500, parsed.PowerMw);
        Assert.Null(parsed.State);
    }

    // Every state the app can be in, not just the one that happens to be easy to reproduce: a write
    // path carrying only the charging case looks correct on a machine left plugged in.
    [Fact]
    public void Record_StoresEveryStateItCanBeGiven()
    {
        foreach (var state in Enum.GetValues<PowerState>())
        {
            File.Delete(_testFile);   // a fresh file per state, so Single() means this state's row
            BatteryHistoryService.UseTestPath(_testFile);
            BatteryHistoryService.Record(60, 80, 3000, state);

            var loaded = BatteryHistoryService.LoadWindow(TimeSpan.FromHours(1));
            Assert.Equal($"{state}", $"{Assert.Single(loaded).State}");
        }
    }

    /// <summary>The state reaches the CSV as its own column, for every state. Reading it back is not
    /// enough on its own: a writer that dropped the column would still round-trip through a parser
    /// that treats an absent state as null.</summary>
    [Fact]
    public void Record_WritesEveryStateNameIntoTheFile()
    {
        foreach (var state in Enum.GetValues<PowerState>())
        {
            File.Delete(_testFile);   // a fresh file per state, so Single() means this state's row
            BatteryHistoryService.UseTestPath(_testFile);
            BatteryHistoryService.Record(60, 80, 3000, state);

            var row = File.ReadAllLines(BatteryHistoryService.FilePath)[^1];
            // Named by position rather than "the last column": temperature_c now trails it.
            Assert.Equal($"{state}", row.Split(',')[4]);
        }
    }

    [Fact]
    public void FormatThenParse_RoundTripsInstantToTheSecond_AcrossLocalOffset()
    {
        // Format writes local time with the machine's UTC offset and TryParse converts back, so the
        // instant must survive whatever timezone the test runs in: the offset is sugar, not data.
        var sample = new BatterySample(new DateTime(2026, 7, 15, 12, 30, 45, DateTimeKind.Utc), 63, 80, 2200);

        var line = BatteryHistoryService.Format(sample);
        Assert.True(BatteryHistoryService.TryParse(line, out var parsed));

        Assert.Equal(sample.AtUtc, parsed.AtUtc);              // same instant, to the second
        Assert.Equal(DateTimeKind.Utc, parsed.AtUtc.Kind);    // stored representation stays UTC
        // ISO 8601 with a local offset, not a Unix-millis integer.
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}[+-]\d{2}:\d{2},", line);
    }

    [Fact]
    public void TryParse_SkipsHeaderLines()
    {
        // Both header lines must fail TryParse, so the skip-on-unparseable read loops drop them.
        Assert.False(BatteryHistoryService.TryParse(BatteryHistoryService.HeaderComment, out _));
        Assert.False(BatteryHistoryService.TryParse(BatteryHistoryService.HeaderColumns, out _));
    }

    [Fact]
    public void Record_OnFreshFile_WritesHeaderBlock_AndReadSkipsIt()
    {
        // The first Record on a non-existent file writes the header block, then the sample.
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
        // The first LoadWindow prunes the 20-day-old row and rewrites the file; the rewrite must
        // keep the header at the top.
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
        // Written directly rather than through Record, which always timestamps "now".
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

    // Downtime-gap detection

    [Fact]
    public void Record_FirstEverSample_ReportsNoGap()
    {
        // Nothing to compare against, so no gap may be reported against a non-existent predecessor.
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
        // The battery charged while the app was not running: a legitimate reading the caller filters
        // out as "not an anomaly", rather than something this layer hides or clamps.
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
        // The anomaly gate shares the graph's downtime threshold: a hole the graph would not draw as
        // downtime must not produce gap info either, or the two would disagree.
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
        // "None" (0) means the graph draws no breaks, not that overnight drain stops being watched:
        // the anomaly path falls back to its own floor so the safety toast can still fire.
        SettingsService.Current.DowntimeGapMinutes = 0;
        var old = new BatterySample(DateTime.UtcNow.AddHours(-8), 90, null, 0);
        File.WriteAllText(_testFile, BatteryHistoryService.Format(old) + "\n");
        BatteryHistoryService.LoadWindow(TimeSpan.FromDays(1));

        Assert.Equal(TimeSpan.MaxValue, BatteryHistoryService.DowntimeThreshold);
        Assert.Equal(DrainAnomalyPolicy.MinGap, BatteryHistoryService.AnomalyGapThreshold);

        var gap = BatteryHistoryService.Record(60, null, 0);

        Assert.NotNull(gap);
        Assert.Equal(30, gap!.Value.SocDropPercent);          // 90 → 60
        Assert.True(gap.Value.GapDuration >= TimeSpan.FromHours(7.9));
    }

    [Fact]
    public void Record_GapDetectionNone_ReportsNoGapForShortHole()
    {
        // The "None" fallback is the anomaly floor, not zero, so a hole shorter than the floor is
        // still not downtime.
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
        // With no LoadWindow call, Record has to seed _lastPersisted by tail-reading the file, or the
        // first Record after a restart sees no gap.
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
        // Larger than the 8 KB tail window, so ReadLastSampleFromFile seeks rather than reading the
        // whole file, and must drop its truncated first line while still returning the true last row.
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
        // With only a 1h window loaded, the sample from before an overnight downtime falls outside
        // _window. Comparing against _window[^1] rather than the last persisted sample would miss
        // the overnight drain, which is the case the feature exists for.
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
