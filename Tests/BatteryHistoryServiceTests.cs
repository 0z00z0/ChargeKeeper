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

    [Theory]
    [InlineData("2026-01-01T12:00:00+00:00,75,80,4500")]           // four columns — written before the state
    public void TryParse_LeavesAnUnreadableStateNull_WithoutLosingTheRow(string line)
    {
        Assert.True(BatteryHistoryService.TryParse(line, out var parsed));
        Assert.Equal(75, parsed.Soc);
        Assert.Equal(4500, parsed.PowerMw);
        Assert.Null(parsed.State);
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

    // Downtime-gap detection

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
