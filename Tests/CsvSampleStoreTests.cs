using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

// CsvSampleStore is the low-level file plumbing shared by the two history services
// (BatteryHistoryService, BatteryCapacityHistoryService). These tests exercise it directly against
// isolated temp files (via UseTestPath), so they never touch a real %AppData%\ChargeKeeper CSV. The
// store does no locking of its own — its owning service holds a lock around every call — so these
// single-threaded tests match its real usage.
public class CsvSampleStoreTests : IDisposable
{
    private readonly string _testFile =
        Path.Combine(Path.GetTempPath(), $"ck-csvstore-test-{Guid.NewGuid():N}.csv");

    // The constructor filename is immediately overridden by UseTestPath, so its value is irrelevant.
    private readonly CsvSampleStore _store = new("unit-test-placeholder.csv");

    public CsvSampleStoreTests() => _store.UseTestPath(_testFile);

    public void Dispose()
    {
        try { File.Delete(_testFile); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void ReadAllLines_NoFileYet_ReturnsEmpty()
    {
        Assert.Empty(_store.ReadAllLines());
    }

    [Fact]
    public void ReadLastLine_NoFileYet_ReturnsNull()
    {
        Assert.Null(_store.ReadLastLine());
    }

    [Fact]
    public void FilePath_ReflectsUseTestPath()
    {
        Assert.Equal(_testFile, _store.FilePath);
    }

    [Fact]
    public void AppendLine_CreatesTheContainingDirectoryOnFirstWrite()
    {
        // Point at a file inside a not-yet-existing subdirectory to prove AppendLine ensures the dir
        // (the dir-ensure-once behaviour both history services relied on) rather than throwing.
        var nestedDir  = Path.Combine(Path.GetTempPath(), $"ck-csvstore-dir-{Guid.NewGuid():N}");
        var nestedFile = Path.Combine(nestedDir, "history.csv");
        try
        {
            _store.UseTestPath(nestedFile);
            _store.AppendLine("hello");

            Assert.True(File.Exists(nestedFile));
            Assert.Equal("hello", Assert.Single(_store.ReadAllLines()));
        }
        finally
        {
            try { Directory.Delete(nestedDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void AppendLine_ThenReadAllLines_ReturnsEveryLineOldestToNewest()
    {
        _store.AppendLine("one");
        _store.AppendLine("two");
        _store.AppendLine("three");

        Assert.Equal(new[] { "one", "two", "three" }, _store.ReadAllLines());
    }

    [Fact]
    public void ReadLastLine_ReturnsMostRecentlyAppendedLine()
    {
        _store.AppendLine("first");
        _store.AppendLine("last");

        Assert.Equal("last", _store.ReadLastLine());
    }

    [Fact]
    public void UseTestPath_IsolatesFromThePreviousFile()
    {
        _store.AppendLine("in-first-file");

        var otherFile = Path.Combine(Path.GetTempPath(), $"ck-csvstore-test2-{Guid.NewGuid():N}.csv");
        try
        {
            _store.UseTestPath(otherFile);

            // Now pointed at a brand-new file — the previous file's content must not be visible, and
            // the dir-ensured flag must have reset so this file still gets written.
            Assert.Empty(_store.ReadAllLines());
            _store.AppendLine("in-second-file");
            Assert.Equal("in-second-file", Assert.Single(_store.ReadAllLines()));
        }
        finally
        {
            try { File.Delete(otherFile); } catch { /* best-effort */ }
        }
    }
}
