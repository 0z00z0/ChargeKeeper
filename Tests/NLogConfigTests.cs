using ChargeKeeper.Services;
using NLog;
using NLog.Config;
using NLog.Layouts;
using NLog.Targets;
using NLog.Targets.Wrappers;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// Guards the shipped nlog.config. Every failure it catches is silent — NLog ignores an unknown
/// attribute and logs nothing at all when the config is missing — so "it built" is not evidence
/// that it logs.
/// </summary>
public class NLogConfigTests
{
    private static LoggingConfiguration LoadShippedConfigStrictly() => ShippedNLogConfig.LoadStrictly();

    private static RetryingTargetWrapper WrapperOf(LoggingConfiguration config, string name = "appfile") =>
        (RetryingTargetWrapper)config.FindTargetByName(name)!;

    private static FileTarget FileTargetOf(LoggingConfiguration config, string name = "appfile") =>
        (FileTarget)WrapperOf(config, name).WrappedTarget!;

    /// <summary>RetryCount/RetryDelayMilliseconds are Layout&lt;int&gt;, so they compare as rendered text.</summary>
    private static string Rendered(Layout<int> value) => value.Render(LogEventInfo.CreateNullEvent());

    [Fact]
    public void ShippedConfig_IsConcurrentWriterSafe()
    {
        // Open-per-write plus a bounded retry is what makes concurrent appends safe. Measured: NLog's
        // keepFileOpen="true" default loses ~70 lines per 720 across 6 concurrent processes, silently.
        var config = LoadShippedConfigStrictly();

        var wrapper = Assert.IsType<RetryingTargetWrapper>(config.FindTargetByName("appfile"));

        // Exact values, not merely "not zero": NLog's own defaults (3 x 100ms) are non-zero too, so a
        // config that lost both attributes would sail through a not-zero check.
        Assert.Equal("5",  Rendered(wrapper.RetryCount));
        Assert.Equal("20", Rendered(wrapper.RetryDelayMilliseconds));
        Assert.False(FileTargetOf(config).KeepFileOpen,
            "keepFileOpen must stay false — an exclusive handle makes sibling ChargeKeeper processes " +
            "(a watchdog probe, an ordinary duplicate launch) lose their log lines silently. That is #34.");
    }

    [Fact]
    public void ShippedConfig_IsCopiedNextToTheBuiltAssembly()
    {
        // NLog discovers nlog.config beside the exe. Without the csproj's CopyToOutputDirectory the
        // file stays in the repo, NLog finds no config, and logs nothing — with no error.
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "nlog.config")),
            $"nlog.config is missing from the build output ({AppContext.BaseDirectory}). Check the " +
            "Content item + CopyToOutputDirectory in ChargeKeeper.csproj — NLog would silently log nothing.");
    }

    // Retention - driven, not parsed

    /// <summary>
    /// A throwaway copy of the log. Every write goes through a freshly loaded copy of the shipped
    /// config with the file name redirected here, so nothing reaches the real per-user log
    /// directory, and each call re-probes the file's age exactly as a restarted process would.
    /// </summary>
    private sealed class TempTrail : IDisposable
    {
        public string Dir { get; } =
            Path.Combine(Path.GetTempPath(), $"ck-nlogconfig-{Guid.NewGuid():N}");

        public string AppFile => Path.Combine(Dir, "app.log");

        public TempTrail() => Directory.CreateDirectory(Dir);

        /// <summary>Writes through <see cref="AppLog.Write"/>, the one writer AppLog and PowerLog share.</summary>
        public void Write(params (string Logger, string CallerFile, string Message)[] entries)
        {
            var config = LoadShippedConfigStrictly();
            FileTargetOf(config).FileName = AppFile;
            var factory = new LogFactory { Configuration = config };
            try
            {
                foreach (var (logger, callerFile, message) in entries)
                    AppLog.Write(factory.GetLogger(logger), LogLevel.Info, message, callerFile);
                factory.Flush();
            }
            finally { factory.Shutdown(); }
        }

        public static void Age(string file, int days)
        {
            var when = DateTime.Now.AddDays(-days);
            File.SetCreationTime(file, when);
            File.SetLastWriteTime(file, when);
        }

        public string Archive(string stem, int daysAgo) =>
            Path.Combine(Dir, $"{stem}_{DateTime.Now.AddDays(-daysAgo):yyyy-MM-dd}_00.log");

        public void PlantArchive(string stem, int daysAgo)
        {
            var path = Archive(stem, daysAgo);
            File.WriteAllText(path, $"an archive from {daysAgo} days ago");
            Age(path, daysAgo);
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    private const string AppCaller = @"X:\src\BatteryMonitor.cs";
    private const string PowerCaller = @"X:\src\LidDelayPolicy.cs";

    [Theory]
    [InlineData("app")]
    public void ShippedConfig_ActuallyKeepsSevenDailyArchivesAndDeletesTheRest(string stem)
    {
        // Deletion is the half that fails silently: nothing in the app notices archives piling up.
        using var trail = new TempTrail();
        for (var age = 1; age <= 9; age++)
            trail.PlantArchive(stem, age);

        // The trails share their directory with the settings file and the battery history, so the
        // sweep must reach archives of this trail and nothing else.
        var bystander = Path.Combine(trail.Dir, "settings.json");
        File.WriteAllText(bystander, "{}");
        TempTrail.Age(bystander, 400);

        trail.Write((AppLog.LoggerName, AppCaller, "an entry"),
                    (PowerLog.LoggerName, PowerCaller, "a power event"));

        Assert.True(File.Exists(bystander), "the sweep must not delete files that are not its archives.");
        for (var age = 1; age <= 7; age++)
            Assert.True(File.Exists(trail.Archive(stem, age)),
                $"the {age}-day-old archive is one of the seven most recent and must survive.");
        Assert.False(File.Exists(trail.Archive(stem, 8)), "the eighth archive back must be deleted.");
        Assert.False(File.Exists(trail.Archive(stem, 9)), "the ninth archive back must be deleted.");
    }

    [Theory]
    [InlineData("app.log", "app")]
    public void ShippedConfig_KeepsTheArchiveMovedFromALongLivedLogFile(string fileName, string stem)
    {
        // The reason retention counts archives instead of ageing them. Windows carries a file's
        // creation time over when NLog moves it to its archive name, and maxArchiveDays reads exactly
        // that timestamp - measured both ways - so an age rule archives a log file created weeks ago
        // and deletes it in the same write. That takes out the whole file on the first run after an
        // upgrade, and a week's worth every time the machine sits unused for a week.
        using var trail = new TempTrail();
        trail.Write((AppLog.LoggerName, AppCaller, "entries nobody has read yet"),
                    (PowerLog.LoggerName, PowerCaller, "a power event nobody has read yet"));

        var active = Path.Combine(trail.Dir, fileName);
        File.SetCreationTime(active, DateTime.Now.AddDays(-56));
        File.SetLastWriteTime(active, DateTime.Now.AddDays(-1));

        trail.Write((AppLog.LoggerName, AppCaller, "the first entry after the upgrade"),
                    (PowerLog.LoggerName, PowerCaller, "a power event after the upgrade"));
        // A second start, so the sweep meets the archive rather than racing its creation.
        trail.Write((AppLog.LoggerName, AppCaller, "an entry after a restart"),
                    (PowerLog.LoggerName, PowerCaller, "a power event after a restart"));

        var archive = trail.Archive(stem, 1);
        Assert.True(File.Exists(archive),
            $"{fileName} was archived and then deleted. Its content is gone: an archive keeps the " +
            "creation time of the file it was moved from, so retention must not be age-based.");
        Assert.Contains("nobody has read yet", File.ReadAllText(archive), StringComparison.Ordinal);
    }
}
