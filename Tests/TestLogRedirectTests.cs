using ChargeKeeper.Services;
using NLog;
using NLog.Targets;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// Holds <see cref="TestLogRedirect"/> to its promise: a test run must not append to the log an
/// installed ChargeKeeper is writing. Without these, the redirect could stop working — a renamed
/// target, a second config assignment, a module initialiser that silently swallowed its own failure
/// — and the only symptom would be fixtures appearing in the user's app.log, which no test reads.
/// </summary>
public class TestLogRedirectTests
{
    /// <summary>Reads a log file the way another writer allows: NLog keeps no handle, but an
    /// installed ChargeKeeper writing the real file concurrently still must not fail the read.</summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void TheModuleInitialiserActuallyRan()
    {
        // It swallows its own exceptions so a failure cannot take the whole run down as a
        // TypeInitializationException. This is where that failure surfaces instead.
        Assert.NotNull(LogManager.Configuration);
        Assert.True(Directory.Exists(TestLogRedirect.Directory),
                    $"the redirect directory was never created: {TestLogRedirect.Directory}");
    }

    [Fact]
    public void NoFileTargetInThisProcessResolvesIntoTheRealPerUserDirectory()
    {
        var targets = LogManager.Configuration!.AllTargets.OfType<FileTarget>().ToArray();
        Assert.NotEmpty(targets);

        foreach (var target in targets)
        {
            string path = target.FileName.Render(LogEventInfo.CreateNullEvent());
            Assert.False(TestLogRedirect.IsUnderRealDataDirectory(path),
                $"target '{target.Name}' writes to {path}, inside the real per-user directory " +
                $"{TestLogRedirect.RealDataDirectory}. A test run would interleave with the " +
                 "installed app's own log.");
        }
    }

    // The static assertions above check where the configuration points. These two check where a
    // line actually lands, which is the thing that matters and the only way to catch a second
    // configuration assigned after the module initialiser ran.

    [Fact]
    public void AppLogInfo_LandsInTheRedirectedFileAndNotTheUserLog()
    {
        string marker = $"redirect-probe-{Guid.NewGuid():N}";
        AppLog.Info(marker);
        LogManager.Flush();

        string redirected = Path.Combine(TestLogRedirect.Directory, "app.log");
        Assert.True(File.Exists(redirected), $"nothing was written to {redirected}");
        Assert.Contains(marker, ReadShared(redirected), StringComparison.Ordinal);

        string real = Path.Combine(TestLogRedirect.RealDataDirectory, "app.log");
        if (File.Exists(real))
            Assert.DoesNotContain(marker, ReadShared(real), StringComparison.Ordinal);
    }

    [Fact]
    public void PowerLogEvent_LandsInTheRedirectedFileAndNotTheUserLog()
    {
        // power.log is a second target under the same configuration, so it can be redirected
        // separately from app.log and has to be asserted separately.
        string marker = $"redirect-probe-{Guid.NewGuid():N}";
        PowerLog.Event(marker, "TestLogRedirectTests");
        LogManager.Flush();

        string redirected = Path.Combine(TestLogRedirect.Directory, PowerLog.FileName);
        Assert.True(File.Exists(redirected), $"nothing was written to {redirected}");
        Assert.Contains(marker, ReadShared(redirected), StringComparison.Ordinal);

        string real = Path.Combine(TestLogRedirect.RealDataDirectory, PowerLog.FileName);
        if (File.Exists(real))
            Assert.DoesNotContain(marker, ReadShared(real), StringComparison.Ordinal);
    }
}
