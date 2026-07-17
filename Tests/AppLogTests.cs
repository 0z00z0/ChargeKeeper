using ChargeKeeper.Services;
using NLog;
using NLog.Config;
using NLog.Targets;
using NLog.Targets.Wrappers;
using Xunit;

namespace ChargeKeeper.Tests;

// AppLog.Info/Error always target the single process-wide %AppData%\ChargeKeeper\app.log file (the
// logger and its config are process-wide statics, not injectable), so these tests must NOT call them
// — they would write to the real user's log. Instead they build the SAME configuration AppLog would
// use (AppLog.BuildFallbackConfiguration, which NLogConfigTests separately pins to the shipped
// nlog.config), redirect its file target to an isolated temp file, and drive a logger through it.
//
// That keeps the pre-NLog coverage meaningful: these are the #34 assertions (concurrent writers lose
// no lines; the timestamp carries a truthful offset) re-aimed at the code that now does the writing.
// Before NLog they exercised AppLog.WriteLine(path, level, message); NLog owns that seam now, so the
// target's FileName is the injection point.
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
    /// A private LogFactory (not the global LogManager) writing AppLog's real configuration to an
    /// isolated file — so tests can run in parallel and never touch %AppData%\ChargeKeeper\app.log.
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
        // AppLog.Error folds the exception into the message ("{source}\n{ex}") instead of handing NLog
        // a typed Exception, so this guards that the layout still renders a stack trace at all — a
        // ${message}-only layout would silently drop an exception passed the idiomatic NLog way.
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
        // Regression guard for the "DateTime.Now:u" bug: ":u" formats with a trailing literal "Z"
        // even though DateTime.Now is LOCAL time, which lies about the offset. The layout stamps an
        // explicit "zzz" offset instead, so the file should show a real "+hh:mm"/"-hh:mm" offset and
        // never a bare trailing "Z".
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
        // The existing %AppData%\ChargeKeeper\app.log must stay readable by eye (and by anything
        // parsing it) across the swap: "[2026-07-16 22:54:09 +02:00] INFO message", LF-terminated,
        // with a blank line between entries. CRLF here would be a silent format change — NLog's
        // LineEnding default is CRLF, so this is a real mutation to guard.
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
        // The #34 scenario, in-process half. The old File.AppendAllText (default FileShare.Read,
        // deny-write) threw on a collision and a bare catch{} silently dropped the line. NLog reports
        // a dropped write NOWHERE — not even to its internal log — so a silent loss here would look
        // exactly like success; hence an exact-count assert rather than a "most lines arrived" one.
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
        // Proves the whole point of adopting NLog: app.log no longer grows unbounded. Driven at a
        // small size for test speed — NLogConfigTests separately asserts the SHIPPED threshold is
        // 10 MB, so this covers the mechanism and that covers the number.
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
        // The public contract is "logging must never throw" — call sites are ~73 fire-and-forget
        // statements, several on startup/crash paths where a throw would take the app down.
        var factory = NewLogFactory(f =>
        {
            f.FileName = Path.Combine(_dir, "no-such-dir", "app.log");
            f.CreateDirs = false;
        });

        var exception = Record.Exception(() => factory.GetLogger(AppLog.LoggerName).Error("should not throw"));

        Assert.Null(exception);
    }
}
