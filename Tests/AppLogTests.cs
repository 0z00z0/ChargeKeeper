using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

// AppLog.Info/Error always target the single process-wide %AppData%\ChargeKeeper\app.log file (the
// path is a readonly static, not injectable) and the point of #34 was specifically its file-sharing
// behaviour under concurrent writers — so these tests drive AppLog.WriteLine(path, level, message)
// directly against isolated temp files. WriteLine is the exact code path Info/Error funnel into
// (Write(level, message) just adds the one-time directory-ensure step before calling it), so
// exercising it covers the real fix without touching the shared app.log.
public class AppLogTests : IDisposable
{
    private readonly string _testFile =
        Path.Combine(Path.GetTempPath(), $"ck-applog-test-{Guid.NewGuid():N}.log");

    public void Dispose()
    {
        try { File.Delete(_testFile); } catch { /* best-effort cleanup */ }
        try
        {
            foreach (var fallback in Directory.GetFiles(Path.GetTempPath(), $"{Path.GetFileName(_testFile)}.fallback-*.log"))
                File.Delete(fallback);
        }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public void WriteLine_SingleCall_WritesOneLineContainingLevelAndMessage()
    {
        AppLog.WriteLine(_testFile, "INFO", "hello world");

        var text = File.ReadAllText(_testFile);
        Assert.Contains("INFO", text);
        Assert.Contains("hello world", text);
    }

    [Fact]
    public void WriteLine_StampsATimestampWithATruthfulOffset_NotAMisleadingUtcZ()
    {
        // Regression guard for the "DateTime.Now:u" bug: ":u" formats with a trailing literal "Z"
        // even though DateTime.Now is LOCAL time, which lies about the offset. The fix stamps
        // DateTimeOffset.Now with an explicit "zzz" offset instead, so the file should show a real
        // "+hh:mm"/"-hh:mm" offset and never a bare trailing "Z".
        AppLog.WriteLine(_testFile, "INFO", "timestamp check");

        var text = File.ReadAllText(_testFile);
        var opening = text.IndexOf('[');
        var closing = text.IndexOf(']', opening);
        Assert.True(opening >= 0 && closing > opening, "expected a bracketed timestamp prefix");

        var timestamp = text[(opening + 1)..closing];
        Assert.DoesNotContain("Z", timestamp);
        Assert.True(
            System.Text.RegularExpressions.Regex.IsMatch(timestamp, @"[+-]\d{2}:\d{2}$"),
            $"expected timestamp to end with a numeric UTC offset, got '{timestamp}'");
    }

    [Fact]
    public void WriteLine_MultipleSequentialCalls_AppendsEachLineRatherThanOverwriting()
    {
        AppLog.WriteLine(_testFile, "INFO", "first");
        AppLog.WriteLine(_testFile, "INFO", "second");
        AppLog.WriteLine(_testFile, "ERROR", "third");

        var text = File.ReadAllText(_testFile);
        Assert.Contains("first", text);
        Assert.Contains("second", text);
        Assert.Contains("third", text);
        Assert.True(text.IndexOf("first", StringComparison.Ordinal) < text.IndexOf("second", StringComparison.Ordinal));
        Assert.True(text.IndexOf("second", StringComparison.Ordinal) < text.IndexOf("third", StringComparison.Ordinal));
    }

    [Fact]
    public void WriteLine_ConcurrentWritersFromManyThreads_LoseNoLines()
    {
        // This is the actual #34 scenario: multiple ChargeKeeper-process-like writers hammering the
        // same file concurrently. The old File.AppendAllText (default FileShare.Read, deny-write)
        // would throw IOException on a collision and the bare catch{} would silently drop the line.
        // FileMode.Append + FileShare.ReadWrite makes each append atomic at EOF, so every one of
        // these lines must survive.
        const int threadCount = 8;
        const int linesPerThread = 25;

        var threads = new Thread[threadCount];
        for (var t = 0; t < threadCount; t++)
        {
            var threadIndex = t;
            threads[t] = new Thread(() =>
            {
                for (var i = 0; i < linesPerThread; i++)
                    AppLog.WriteLine(_testFile, "INFO", $"thread-{threadIndex}-line-{i}");
            });
        }

        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();

        var lineCount = File.ReadAllText(_testFile)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Count(line => line.StartsWith('['));

        Assert.Equal(threadCount * linesPerThread, lineCount);
    }

    [Fact]
    public void WriteLine_TargetPathIsAnUnwritableDirectory_NeverThrows()
    {
        // A path whose directory doesn't exist (and WriteLine never creates one — that's Write's
        // job via EnsureDirectory) exercises the final drop-and-fallback path. The public contract
        // ("logging must never throw") must hold even when every attempt fails.
        var unwritablePath = Path.Combine(
            Path.GetTempPath(), $"ck-applog-missing-dir-{Guid.NewGuid():N}", "app.log");

        var exception = Record.Exception(() => AppLog.WriteLine(unwritablePath, "ERROR", "should not throw"));

        Assert.Null(exception);
    }
}
