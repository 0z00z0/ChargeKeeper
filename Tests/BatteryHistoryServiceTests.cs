using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

// BatteryHistoryService is a static class writing to a fixed AppData path in production; each
// test points it at an isolated temp file via UseTestPath (which also resets all in-memory state),
// so tests never touch the real user's history.csv and can't see each other's data.
public class BatteryHistoryServiceTests : IDisposable
{
    private readonly string _testFile =
        Path.Combine(Path.GetTempPath(), $"lpt-history-test-{Guid.NewGuid():N}.csv");

    public BatteryHistoryServiceTests() => BatteryHistoryService.UseTestPath(_testFile);

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

        var rawLine = Assert.Single(File.ReadAllLines(_testFile));
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
