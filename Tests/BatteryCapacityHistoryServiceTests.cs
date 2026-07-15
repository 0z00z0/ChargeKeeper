using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

// Same isolation pattern as BatteryHistoryServiceTests: UseTestPath points the static service at a
// throwaway file and resets its in-memory "already recorded today" cache, so tests never touch the
// real user's battery-capacity-history.csv and can't see each other's data.
public class BatteryCapacityHistoryServiceTests : IDisposable
{
    private readonly string _testFile =
        Path.Combine(Path.GetTempPath(), $"lpt-capacity-test-{Guid.NewGuid():N}.csv");

    public BatteryCapacityHistoryServiceTests() => BatteryCapacityHistoryService.UseTestPath(_testFile);

    public void Dispose()
    {
        try { File.Delete(_testFile); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void FormatThenParse_RoundTrips_WithDesignCapacity()
    {
        var sample = new CapacitySample(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc), 48000, 52000);

        var line = BatteryCapacityHistoryService.Format(sample);
        Assert.True(BatteryCapacityHistoryService.TryParse(line, out var parsed));

        Assert.Equal(sample.FullChargeMwh, parsed.FullChargeMwh);
        Assert.Equal(sample.DesignMwh, parsed.DesignMwh);
        Assert.Equal(sample.AtUtc, parsed.AtUtc, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public void FormatThenParse_RoundTrips_WithNullDesignCapacity()
    {
        // Some battery controllers don't report design capacity at all — must round-trip as null,
        // never a fabricated 0.
        var sample = new CapacitySample(DateTime.UtcNow, 45000, null);

        var line = BatteryCapacityHistoryService.Format(sample);
        Assert.True(BatteryCapacityHistoryService.TryParse(line, out var parsed));

        Assert.Null(parsed.DesignMwh);
        Assert.Equal(45000, parsed.FullChargeMwh);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not,enough")]
    [InlineData("abc,48000,52000")]     // non-numeric timestamp
    public void TryParse_RejectsMalformedLine(string line)
    {
        Assert.False(BatteryCapacityHistoryService.TryParse(line, out _));
    }

    [Fact]
    public void FormatThenParse_RoundTripsInstantToTheSecond_AcrossLocalOffset()
    {
        // A whole-second UTC instant must survive Format (written in local time with the machine's
        // UTC offset) → TryParse (converted back to UTC) unchanged, regardless of the dev timezone.
        var sample = new CapacitySample(new DateTime(2026, 7, 15, 9, 5, 30, DateTimeKind.Utc), 48000, 52000);

        var line = BatteryCapacityHistoryService.Format(sample);
        Assert.True(BatteryCapacityHistoryService.TryParse(line, out var parsed));

        Assert.Equal(sample.AtUtc, parsed.AtUtc);
        Assert.Equal(DateTimeKind.Utc, parsed.AtUtc.Kind);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}[+-]\d{2}:\d{2},", line);
    }

    [Fact]
    public void TryParse_SkipsHeaderLines()
    {
        Assert.False(BatteryCapacityHistoryService.TryParse(BatteryCapacityHistoryService.HeaderComment, out _));
        Assert.False(BatteryCapacityHistoryService.TryParse(BatteryCapacityHistoryService.HeaderColumns, out _));
    }

    [Fact]
    public void RecordIfNewDay_OnFreshFile_WritesHeaderBlock_AndReadSkipsIt()
    {
        // The first RecordIfNewDay on a non-existent file writes the descriptive header block, then
        // the sample. LoadAll skips the header and returns only the data row.
        BatteryCapacityHistoryService.RecordIfNewDay(48000, 52000);

        var lines = File.ReadAllLines(_testFile);
        Assert.Equal(BatteryCapacityHistoryService.HeaderComment, lines[0]);
        Assert.Equal(BatteryCapacityHistoryService.HeaderColumns, lines[1]);
        Assert.StartsWith("#", lines[0]);
        Assert.Equal(3, lines.Length);   // comment + columns + one data row

        var sample = Assert.Single(BatteryCapacityHistoryService.LoadAll());
        Assert.Equal(48000, sample.FullChargeMwh);
    }

    [Fact]
    public void RecordIfNewDay_FirstCallToday_WritesOneRow()
    {
        BatteryCapacityHistoryService.RecordIfNewDay(48000, 52000);

        var all = BatteryCapacityHistoryService.LoadAll();

        var sample = Assert.Single(all);
        Assert.Equal(48000, sample.FullChargeMwh);
        Assert.Equal(52000, sample.DesignMwh);
    }

    [Fact]
    public void RecordIfNewDay_SecondCallSameDay_IsNoOp()
    {
        BatteryCapacityHistoryService.RecordIfNewDay(48000, 52000);
        BatteryCapacityHistoryService.RecordIfNewDay(47000, 52000); // later the same process, same day

        Assert.Single(BatteryCapacityHistoryService.LoadAll());
    }

    [Fact]
    public void RecordIfNewDay_SameDayAcrossRestart_DoesNotDuplicate()
    {
        // Simulate an earlier process run today by writing a row directly, then resetting only the
        // in-memory cache (UseTestPath) the way a real app restart would — RecordIfNewDay's file
        // check (not just the memory cache) must still catch it.
        var today = new CapacitySample(DateTime.UtcNow, 48000, 52000);
        File.WriteAllText(_testFile, BatteryCapacityHistoryService.Format(today) + "\n");
        BatteryCapacityHistoryService.UseTestPath(_testFile); // reset in-memory cache, keep the file

        BatteryCapacityHistoryService.RecordIfNewDay(47000, 52000);

        Assert.Single(BatteryCapacityHistoryService.LoadAll());
    }

    [Fact]
    public void RecordIfNewDay_DifferentDay_WritesSecondRow()
    {
        var yesterday = new CapacitySample(DateTime.UtcNow.AddDays(-1), 49000, 52000);
        File.WriteAllText(_testFile, BatteryCapacityHistoryService.Format(yesterday) + "\n");
        BatteryCapacityHistoryService.UseTestPath(_testFile);

        BatteryCapacityHistoryService.RecordIfNewDay(48000, 52000);

        Assert.Equal(2, BatteryCapacityHistoryService.LoadAll().Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void RecordIfNewDay_NonPositiveFullCharge_NeverLogsGarbage(int fullChargeMwh)
    {
        BatteryCapacityHistoryService.RecordIfNewDay(fullChargeMwh, 52000);

        Assert.Empty(BatteryCapacityHistoryService.LoadAll());
    }

    [Fact]
    public void LoadAll_NoFileYet_ReturnsEmpty()
    {
        Assert.Empty(BatteryCapacityHistoryService.LoadAll());
    }
}
