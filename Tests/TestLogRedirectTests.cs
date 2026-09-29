using ChargeKeeper.Services;
using NLog;
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

        string real = AppPaths.LogFile(AppLog.FileName);
        if (File.Exists(real))
            Assert.DoesNotContain(marker, ReadShared(real), StringComparison.Ordinal);
    }
}
