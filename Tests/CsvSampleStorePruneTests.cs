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

    // ── The row cap ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheCapAndTheAgeRuleBothApply()
    {
        Seed(1, 2, 3, 4, 5, 6);

        // Two dropped for age, then one more for the cap.
        Assert.Equal(3, _store.Prune(l => KeepAtLeast(l, 3), maxRows: 3));
        Assert.Equal(["4", "5", "6"], Rows());
    }

    /// <summary>Omitting the cap is what the battery history does, and must not bound anything.</summary>
    [Fact]
    public void NoCapMeansNoCap()
    {
        Seed(Enumerable.Range(1, 500).ToArray());

        Assert.Equal(0, _store.Prune(l => KeepAtLeast(l, 0)));
        Assert.Equal(500, Rows().Length);
    }
}
