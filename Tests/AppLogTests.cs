using ChargeKeeper.Services;
using NLog;
using NLog.Config;
using NLog.Targets;
using NLog.Targets.Wrappers;
using Xunit;

namespace ChargeKeeper.Tests;

// AppLog.Info/Error are process-wide statics writing to the real %AppData%\ChargeKeeper\app.log, so
// they are never called here. These tests build the same configuration AppLog uses, redirect its
// file target to an isolated temp file, and drive a logger through that.
public class AppLogTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"ck-applog-test-{Guid.NewGuid():N}");
    private readonly string _testFile;

    public AppLogTests()
    {
        Directory.CreateDirectory(_dir);
        _testFile = Path.Combine(_dir, "app.log");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A private LogFactory rather than the global LogManager, writing AppLog's real configuration
    /// to an isolated file, so tests can run in parallel without touching the user's app.log.
    /// </summary>
    private LogFactory NewLogFactory(Action<FileTarget>? tweak = null)
    {
        var config = AppLog.BuildFallbackConfiguration();
        var file = FileTargetOf(config);
        file.FileName = _testFile;
        tweak?.Invoke(file);
        return new LogFactory { Configuration = config };
    }

    private static FileTarget FileTargetOf(LoggingConfiguration config) =>
        (FileTarget)((RetryingTargetWrapper)config.FindTargetByName("appfile")).WrappedTarget!;

    private string ReadLog() => File.ReadAllText(_testFile);

    [Fact]
    public void Info_WritesOneLineContainingLevelAndMessage()
    {
        var factory = NewLogFactory();
        factory.GetLogger(AppLog.LoggerName).Info("hello world");
        factory.Flush();

        var text = ReadLog();
        Assert.Contains("INFO", text);
        Assert.Contains("hello world", text);
    }

    [Fact]
    public void Error_RendersTheExceptionAfterTheSourceOnFollowingLines()
    {
        // AppLog.Error folds the exception into the message rather than handing NLog a typed
        // Exception, so a ${message}-only layout would silently drop an idiomatically passed one.
        var factory = NewLogFactory();
        Exception caught;
        try { throw new InvalidOperationException("boom"); }
        catch (Exception ex) { caught = ex; }
        factory.GetLogger(AppLog.LoggerName).Error($"TestSource\n{caught}");
        factory.Flush();

        var text = ReadLog();
        Assert.Contains("ERROR TestSource", text);
        Assert.Contains("InvalidOperationException", text);
        Assert.Contains("boom", text);
    }

    [Fact]
    public void WriteLine_StampsATimestampWithATruthfulOffset_NotAMisleadingUtcZ()
    {
        // ":u" formats with a trailing literal "Z" even though DateTime.Now is local time, which
        // lies about the offset. The layout stamps an explicit "zzz" offset instead.
        var factory = NewLogFactory();
        factory.GetLogger(AppLog.LoggerName).Info("timestamp check");
        factory.Flush();

        var text = ReadLog();
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
    public void LineFormat_MatchesTheFormatWrittenBeforeNLog()
    {
        // app.log stays readable by eye and by anything parsing it: LF-terminated, one blank line
        // between entries. NLog's LineEnding default is CRLF, so the setting is load-bearing.
        var factory = NewLogFactory();
        var log = factory.GetLogger(AppLog.LoggerName);
        log.Info("first");
        log.Info("second");
        factory.Flush();

        var text = ReadLog();
        Assert.DoesNotContain("\r", text);
        Assert.Contains("\n\n", text);
        Assert.Matches(@"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} [+-]\d{2}:\d{2}\] INFO first\n\n", text);
    }

    [Fact]
    public void MultipleSequentialCalls_AppendsEachLineRatherThanOverwriting()
    {
        var factory = NewLogFactory();
        var log = factory.GetLogger(AppLog.LoggerName);
        log.Info("first");
        log.Info("second");
        log.Error("third");
        factory.Flush();

        var text = ReadLog();
        Assert.True(text.IndexOf("first", StringComparison.Ordinal) < text.IndexOf("second", StringComparison.Ordinal));
        Assert.True(text.IndexOf("second", StringComparison.Ordinal) < text.IndexOf("third", StringComparison.Ordinal));
    }

    [Fact]
    public void ConcurrentWritersFromManyThreads_LoseNoLines()
    {
        // NLog reports a dropped write nowhere, not even to its internal log, so a silent loss would
        // look exactly like success. Hence an exact count rather than "most lines arrived".
        const int threadCount = 8;
        const int linesPerThread = 25;

        var factory = NewLogFactory();
        var log = factory.GetLogger(AppLog.LoggerName);

        var threads = new Thread[threadCount];
        for (var t = 0; t < threadCount; t++)
        {
            var threadIndex = t;
            threads[t] = new Thread(() =>
            {
                for (var i = 0; i < linesPerThread; i++)
                    log.Info($"thread-{threadIndex}-line-{i}");
            });
        }

        foreach (var thread in threads) thread.Start();
        foreach (var thread in threads) thread.Join();
        factory.Flush();

        var text = ReadLog();
        var lineCount = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(line => line.StartsWith('['));
        Assert.Equal(threadCount * linesPerThread, lineCount);

        // Count alone could pass with duplicates masking a loss; assert every distinct line landed.
        for (var t = 0; t < threadCount; t++)
            for (var i = 0; i < linesPerThread; i++)
                Assert.Contains($"thread-{t}-line-{i}\n", text);
    }

    [Fact]
    public void ArchivesOnceTheFileGrowsPastTheConfiguredSize_AndKeepsTheOldContent()
    {
        // Driven at a small size for speed: this covers the mechanism, while NLogConfigTests pins
        // the shipped threshold.
        const int archiveAbove = 4 * 1024;
        var factory = NewLogFactory(f => f.ArchiveAboveSize = archiveAbove);
        var log = factory.GetLogger(AppLog.LoggerName);

        for (var i = 0; i < 400; i++) log.Info($"padding line {i} {new string('x', 100)}");
        factory.Flush();
        factory.Shutdown();

        var archives = Directory.GetFiles(_dir).Where(f => f != _testFile).ToList();
        Assert.NotEmpty(archives);
        Assert.True(new FileInfo(_testFile).Length <= archiveAbove * 2,
            $"app.log should have been rolled, but is {new FileInfo(_testFile).Length} bytes.");

        // Rotation must ROLL the trail, not shred it: the earliest lines have to survive in an archive.
        var everything = string.Concat(Directory.GetFiles(_dir).Select(File.ReadAllText));
        Assert.Contains("padding line 0 ", everything);
        Assert.Contains("padding line 399 ", everything);
    }

    [Fact]
    public void Logging_NeverThrows_EvenWhenTheTargetPathIsUnwritable()
    {
        // Logging must never throw: the call sites are fire-and-forget, several on startup and crash
        // paths where a throw would take the app down.
        var factory = NewLogFactory(f =>
        {
            f.FileName = Path.Combine(_dir, "no-such-dir", "app.log");
            f.CreateDirs = false;
        });

        var exception = Record.Exception(() => factory.GetLogger(AppLog.LoggerName).Error("should not throw"));

        Assert.Null(exception);
    }
}
