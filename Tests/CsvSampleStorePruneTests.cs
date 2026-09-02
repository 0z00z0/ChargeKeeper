using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

// The retention mechanism itself, exercised directly against isolated temp files. One prune serves
// every sample file in the app: the battery history passes an age rule and no cap, the performance
// log passes both. These hold the shared behaviour so neither caller has to re-test it.
public class CsvSampleStorePruneTests : IDisposable
{
    private readonly string _testFile =
        Path.Combine(Path.GetTempPath(), $"ck-csvprune-test-{Guid.NewGuid():N}.csv");

    private const string Header = "# header comment\ncol";

    private readonly CsvSampleStore _store = new("unit-test-placeholder.csv", Header);

    public CsvSampleStorePruneTests() => _store.UseTestPath(_testFile);

    public void Dispose()
    {
        try { File.Delete(_testFile); } catch { /* best-effort cleanup */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>Rows are numbers; anything else is not a row.</summary>
    private static CsvRowVerdict KeepAtLeast(string line, int floor) =>
        !int.TryParse(line, out int n) ? CsvRowVerdict.NotARow
        : n >= floor                   ? CsvRowVerdict.Keep
                                       : CsvRowVerdict.Expired;

    private void Seed(params int[] rows) =>
        _store.AppendLines([.. rows.Select(r => r.ToString(System.Globalization.CultureInfo.InvariantCulture))]);

    private string[] Rows() => [.. _store.ReadAllLines().Where(l => int.TryParse(l, out _))];

    [Fact]
    public void AnAbsentFileIsNotRewritten()
    {
        Assert.Equal(0, _store.Prune(l => KeepAtLeast(l, 0)));
        Assert.False(File.Exists(_testFile));
    }

    [Fact]
    public void ExpiredRowsAreDroppedAndCounted()
    {
        Seed(1, 2, 3, 4, 5);

        Assert.Equal(2, _store.Prune(l => KeepAtLeast(l, 3)));
        Assert.Equal(["3", "4", "5"], Rows());
    }

    [Fact]
    public void NothingExpiredMeansNoRewriteAtAll()
    {
        Seed(1, 2, 3);
        var before = File.GetLastWriteTimeUtc(_testFile);

        Assert.Equal(0, _store.Prune(l => KeepAtLeast(l, 0)));
        Assert.Equal(before, File.GetLastWriteTimeUtc(_testFile));
    }

    /// <summary>The header fails every caller's row test, so it is dropped as "not a row" — and must
    /// come back on the rewrite, or the file loses the block that explains it.</summary>
    [Fact]
    public void TheHeaderIsReEmittedByTheRewrite()
    {
        Seed(1, 2, 3);

        _store.Prune(l => KeepAtLeast(l, 3));

        var text = File.ReadAllText(_testFile);
        Assert.Contains("# header comment", text, StringComparison.Ordinal);
        Assert.Contains("col", text, StringComparison.Ordinal);
    }

    /// <summary>A file of nothing but header and corruption must not be rewritten on every pass:
    /// lines that are not rows are dropped silently and never counted as expired.</summary>
    [Fact]
    public void LinesThatAreNotRowsDoNotOnTheirOwnTriggerARewrite()
    {
        _store.AppendLines(["not-a-number", "also-not-a-number"]);
        var before = File.GetLastWriteTimeUtc(_testFile);

        Assert.Equal(0, _store.Prune(l => KeepAtLeast(l, 0)));
        Assert.Equal(before, File.GetLastWriteTimeUtc(_testFile));
    }

    // ── The row cap ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Age alone cannot bound a file written at a rate the user chooses, which is why the
    /// shared prune takes an optional cap. The surplus comes off the OLDEST end.</summary>
    [Fact]
    public void TheRowCapDropsTheOldestSurplus()
    {
        Seed(1, 2, 3, 4, 5, 6);

        Assert.Equal(2, _store.Prune(l => KeepAtLeast(l, 0), maxRows: 4));
        Assert.Equal(["3", "4", "5", "6"], Rows());
    }

    [Fact]
    public void TheCapAndTheAgeRuleBothApply()
    {
        Seed(1, 2, 3, 4, 5, 6);

        // Two dropped for age, then one more for the cap.
        Assert.Equal(3, _store.Prune(l => KeepAtLeast(l, 3), maxRows: 3));
        Assert.Equal(["4", "5", "6"], Rows());
    }

    [Fact]
    public void AFileUnderTheCapIsLeftAlone()
    {
        Seed(1, 2, 3);
        var before = File.GetLastWriteTimeUtc(_testFile);

        Assert.Equal(0, _store.Prune(l => KeepAtLeast(l, 0), maxRows: 10));
        Assert.Equal(before, File.GetLastWriteTimeUtc(_testFile));
    }

    /// <summary>Omitting the cap is what the battery history does, and must not bound anything.</summary>
    [Fact]
    public void NoCapMeansNoCap()
    {
        Seed(Enumerable.Range(1, 500).ToArray());

        Assert.Equal(0, _store.Prune(l => KeepAtLeast(l, 0)));
        Assert.Equal(500, Rows().Length);
    }

    // ── The batched append the fast rate needs ──────────────────────────────────────────────────

    [Fact]
    public void AnEmptyBatchWritesNothingAndCreatesNoFile()
    {
        _store.AppendLines([]);

        Assert.False(File.Exists(_testFile));
    }

    [Fact]
    public void ABatchAndOneLineAtATimeProduceTheSameFile()
    {
        var other = Path.Combine(Path.GetTempPath(), $"ck-csvprune-other-{Guid.NewGuid():N}.csv");
        var single = new CsvSampleStore("unit-test-placeholder.csv", Header);
        single.UseTestPath(other);
        try
        {
            _store.AppendLines(["1", "2", "3"]);
            single.AppendLine("1");
            single.AppendLine("2");
            single.AppendLine("3");

            Assert.Equal(File.ReadAllText(other), File.ReadAllText(_testFile));
        }
        finally
        {
            try { File.Delete(other); } catch { /* best-effort */ }
        }
    }
}
