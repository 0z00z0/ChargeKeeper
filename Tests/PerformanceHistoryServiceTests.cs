using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The performance log: its own file, the battery history's retention mechanism, and a live window
/// the graph draws from. The service is static and writes to a fixed AppData path, so each test
/// points it at an isolated temp file via the same <c>UseTestPath</c> seam the two battery histories
/// carry — the pattern already in the suite rather than a second one.
/// </summary>
public class PerformanceHistoryServiceTests : IDisposable
{
    private readonly string _testFile =
        Path.Combine(Path.GetTempPath(), $"ck-performance-test-{Guid.NewGuid():N}.csv");

    public PerformanceHistoryServiceTests() => PerformanceHistoryService.UseTestPath(_testFile);

    public void Dispose()
    {
        try { File.Delete(_testFile); } catch { /* best-effort cleanup */ }
        GC.SuppressFinalize(this);
    }

    private static ProcessorReading Cpu(DateTime at, double pct) => new(at, pct);

    private static ResourceReading Res(DateTime at) => new(at, 51_200, 61_440, 412, 37);

    // ── Its own file, never the user's ──────────────────────────────────────────────────────────

    /// <summary>Separate from the application log and from both battery histories.</summary>
    [Fact]
    public void TheLogIsItsOwnFile()
    {
        PerformanceHistoryService.UseTestPath(_testFile);   // restore after the assertions below

        Assert.EndsWith("performance-history.csv", new CsvSampleStore("performance-history.csv").FilePath,
                        StringComparison.Ordinal);
        Assert.NotEqual(BatteryHistoryService.FilePath, new CsvSampleStore("performance-history.csv").FilePath);
        Assert.NotEqual(BatteryCapacityHistoryService.FilePath,
                        new CsvSampleStore("performance-history.csv").FilePath);
    }

    /// <summary>A test run must not write into the per-user directory an installed ChargeKeeper is
    /// using. Asserted against the same helper the log redirect is asserted with.</summary>
    [Fact]
    public void ATestRunWritesOutsideTheRealPerUserDirectory()
    {
        Assert.False(TestLogRedirect.IsUnderRealDataDirectory(PerformanceHistoryService.FilePath),
            $"the performance log points at {PerformanceHistoryService.FilePath}, inside the real " +
            $"per-user directory {TestLogRedirect.RealDataDirectory}.");

        // ...while the shipped default does land there, so the seam is what moved it, not an
        // accidental change of the location itself.
        Assert.True(TestLogRedirect.IsUnderRealDataDirectory(
            new CsvSampleStore("performance-history.csv").FilePath));
    }

    // ── The row format ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AProcessorRowRoundTripsAndCarriesNoResourceFields()
    {
        var at = new DateTime(2026, 9, 2, 6, 0, 0, DateTimeKind.Utc);

        Assert.True(PerformanceHistoryService.TryParse(
            PerformanceHistoryService.Format(Cpu(at, 1.25)), out var row));

        Assert.Equal(at, row.AtUtc, TimeSpan.FromMilliseconds(1));
        Assert.Equal(1.25, row.ProcessorPercent!.Value, 3);
        Assert.Null(row.WorkingSetKb);
        Assert.Null(row.PrivateBytesKb);
        Assert.Null(row.Handles);
        Assert.Null(row.Threads);
    }

    [Fact]
    public void AResourceRowRoundTripsAndCarriesNoProcessorField()
    {
        var at = new DateTime(2026, 9, 2, 6, 0, 0, DateTimeKind.Utc);

        Assert.True(PerformanceHistoryService.TryParse(
            PerformanceHistoryService.Format(Res(at)), out var row));

        Assert.Null(row.ProcessorPercent);
        Assert.Equal(51_200, row.WorkingSetKb);
        Assert.Equal(61_440, row.PrivateBytesKb);
        Assert.Equal(412, row.Handles);
        Assert.Equal(37, row.Threads);
    }

    [Fact]
    public void TheHeaderLinesAreNotRows() =>
        Assert.All(PerformanceHistoryService.Header.Split('\n'),
            line => Assert.False(PerformanceHistoryService.TryParse(line, out _)));

    [Theory]
    [InlineData("")]
    [InlineData("not,enough,fields")]
    [InlineData("nonsense,1.0,,,,")]                       // unparseable timestamp
    [InlineData("2026-09-02T06:00:00.000+02:00,,,,,")]     // neither kind of row
    public void AMalformedLineIsNotARow(string line) =>
        Assert.False(PerformanceHistoryService.TryParse(line, out _));

    // ── Buffering and flushing ──────────────────────────────────────────────────────────────────

    [Fact]
    public void NothingReachesTheFileUntilAFlush()
    {
        PerformanceHistoryService.Record(Cpu(DateTime.UtcNow, 1));
        PerformanceHistoryService.Record(Cpu(DateTime.UtcNow, 2));

        Assert.False(File.Exists(_testFile));

        PerformanceHistoryService.Flush();

        Assert.True(File.Exists(_testFile));
        Assert.Equal(2, PerformanceHistoryService.LoadAll().Count);
    }

    [Fact]
    public void AFlushWithNothingBufferedCreatesNoFile()
    {
        PerformanceHistoryService.Flush();

        Assert.False(File.Exists(_testFile));
    }

    [Fact]
    public void ASecondFlushDoesNotRewriteWhatTheFirstAlreadyWrote()
    {
        PerformanceHistoryService.Record(Cpu(DateTime.UtcNow, 1));
        PerformanceHistoryService.Flush();
        PerformanceHistoryService.Flush();

        Assert.Single(PerformanceHistoryService.LoadAll());
    }

    [Fact]
    public void BothKindsOfRowLandInTheOneFile()
    {
        var at = DateTime.UtcNow;
        PerformanceHistoryService.Record(Cpu(at, 3.5));
        PerformanceHistoryService.Record(Res(at));
        PerformanceHistoryService.Flush();

        var rows = PerformanceHistoryService.LoadAll();
        Assert.Equal(2, rows.Count);
        Assert.Single(rows, r => r.ProcessorPercent is not null);
        Assert.Single(rows, r => r.WorkingSetKb is not null);
    }

    // ── The live window the graph draws ─────────────────────────────────────────────────────────

    [Fact]
    public void TheTwoSeriesAreHeldApart()
    {
        var at = DateTime.UtcNow;
        PerformanceHistoryService.Record(Cpu(at, 1));
        PerformanceHistoryService.Record(Cpu(at, 2));
        PerformanceHistoryService.Record(Res(at));

        Assert.Equal(2, PerformanceHistoryService.ProcessorWindow().Count);
        Assert.Single(PerformanceHistoryService.ResourceWindow());
    }

    [Fact]
    public void ReadingsOlderThanTheWindowFallOutOfIt()
    {
        var old = DateTime.UtcNow - PerformanceHistoryService.WindowSpan - TimeSpan.FromMinutes(1);
        PerformanceHistoryService.Record(Cpu(old, 1));
        PerformanceHistoryService.Record(Res(old));
        PerformanceHistoryService.Record(Cpu(DateTime.UtcNow, 2));

        Assert.Single(PerformanceHistoryService.ProcessorWindow());
        Assert.Empty(PerformanceHistoryService.ResourceWindow());
    }

    /// <summary>Falling out of the live window is not the same as being dropped: the row is still in
    /// the log. The window is what the graph shows, retention is what the file keeps.</summary>
    [Fact]
    public void AReadingOutsideTheWindowIsStillWrittenToTheFile()
    {
        var old = DateTime.UtcNow - PerformanceHistoryService.WindowSpan - TimeSpan.FromMinutes(1);
        PerformanceHistoryService.Record(Cpu(old, 1));
        PerformanceHistoryService.Flush();

        Assert.Empty(PerformanceHistoryService.ProcessorWindow());
        Assert.Single(PerformanceHistoryService.LoadAll());
    }

    [Fact]
    public void ClearingTheWindowLeavesTheFileAlone()
    {
        PerformanceHistoryService.Record(Cpu(DateTime.UtcNow, 1));
        PerformanceHistoryService.Flush();

        PerformanceHistoryService.ClearWindow();

        Assert.Empty(PerformanceHistoryService.ProcessorWindow());
        Assert.Single(PerformanceHistoryService.LoadAll());
    }

    // ── Retention ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The first flush of a session prunes, as the battery history prunes on its first
    /// window load, so a stale row never survives to be read back.</summary>
    [Fact]
    public void RowsPastTheRetentionAgeAreDropped()
    {
        var stale = DateTime.UtcNow - TimeSpan.FromDays(PerformanceHistoryService.RetentionDays + 1);
        PerformanceHistoryService.Record(Cpu(stale, 1));
        PerformanceHistoryService.Record(Cpu(DateTime.UtcNow, 2));

        PerformanceHistoryService.Flush();

        var kept = Assert.Single(PerformanceHistoryService.LoadAll());
        Assert.Equal(2, kept.ProcessorPercent!.Value, 3);
    }

    /// <summary>And an explicit prune drops one written after that first pass.</summary>
    [Fact]
    public void APruneDropsAStaleRowWrittenAfterTheFirstPass()
    {
        PerformanceHistoryService.Record(Cpu(DateTime.UtcNow, 1));
        PerformanceHistoryService.Flush();                       // first pass, prunes nothing

        var stale = DateTime.UtcNow - TimeSpan.FromDays(PerformanceHistoryService.RetentionDays + 1);
        PerformanceHistoryService.Record(Cpu(stale, 9));
        PerformanceHistoryService.Flush();

        Assert.Equal(1, PerformanceHistoryService.Prune());
        var kept = Assert.Single(PerformanceHistoryService.LoadAll());
        Assert.Equal(1, kept.ProcessorPercent!.Value, 3);
    }

    [Fact]
    public void RowsInsideTheRetentionAgeSurvive()
    {
        var recent = DateTime.UtcNow - TimeSpan.FromDays(PerformanceHistoryService.RetentionDays - 1);
        PerformanceHistoryService.Record(Cpu(recent, 1));
        PerformanceHistoryService.Flush();

        Assert.Equal(0, PerformanceHistoryService.Prune());
        Assert.Single(PerformanceHistoryService.LoadAll());
    }

    [Fact]
    public void PruningRewritesTheHeaderSoTheFileStaysReadable()
    {
        var stale = DateTime.UtcNow - TimeSpan.FromDays(PerformanceHistoryService.RetentionDays + 1);
        PerformanceHistoryService.Record(Cpu(stale, 1));
        PerformanceHistoryService.Record(Cpu(DateTime.UtcNow, 2));
        PerformanceHistoryService.Flush();

        PerformanceHistoryService.Prune();

        var text = File.ReadAllText(_testFile);
        Assert.Contains(PerformanceHistoryService.HeaderColumns, text, StringComparison.Ordinal);
    }

    /// <summary>Age alone cannot bound this file, because the rate is the user's to choose. The row
    /// cap is why, and it is the same shared prune the battery history uses.</summary>
    [Fact]
    public void TheRowCapIsWhatAgeAloneCannotSupply()
    {
        Assert.True(PerformanceHistoryService.MaxRows > 0,
            "the performance log must carry a row cap: at 10 Hz, seven days of age is millions of rows");
    }
}
