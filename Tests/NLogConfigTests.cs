using System.Globalization;
using System.Reflection;
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
    private const long TenMegabytes = 10L * 1024 * 1024;

    private static LoggingConfiguration LoadShippedConfigStrictly() => ShippedNLogConfig.LoadStrictly();

    private static RetryingTargetWrapper WrapperOf(LoggingConfiguration config, string name = "appfile") =>
        (RetryingTargetWrapper)config.FindTargetByName(name)!;

    private static FileTarget FileTargetOf(LoggingConfiguration config, string name = "appfile") =>
        (FileTarget)WrapperOf(config, name).WrappedTarget!;

    /// <summary>The targets a log event under <paramref name="loggerName"/> would actually reach.</summary>
    private static string[] TargetsFor(LoggingConfiguration config, string loggerName) =>
        [.. config.LoggingRules
                  .Where(r => r.NameMatches(loggerName) && r.IsLoggingEnabledForLevel(LogLevel.Info))
                  .SelectMany(r => r.Targets)
                  .Select(t => t.Name!)];

    /// <summary>RetryCount/RetryDelayMilliseconds are Layout&lt;int&gt;, so they compare as rendered text.</summary>
    private static string Rendered(Layout<int> value) => value.Render(LogEventInfo.CreateNullEvent());

    /// <summary>The shipped file with its comments stripped, so an assertion about what the config
    /// does not say is not defeated by a comment warning readers off that very spelling.</summary>
    private static string SettingsTextOfShippedConfig() =>
        System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(RepoFiles.Find("nlog.config")), "<!--.*?-->", string.Empty,
            System.Text.RegularExpressions.RegexOptions.Singleline);

    [Fact]
    public void ShippedConfig_ParsesWithNoUnknownOrMisspelledSettings() =>
        // Fails loudly on any attribute this NLog version does not recognise, including one removed
        // by a future major-version bump of the NLog package.
        Assert.NotNull(FileTargetOf(LoadShippedConfigStrictly()));

    [Fact]
    public void ShippedConfig_RollsDailyKeepsSevenArchivesAndStillCapsOneDayAt10Mb()
    {
        // The rotation policy has to live in the config file, not in code. archiveAboveSize stays as
        // a within-day cap: a day's file cannot grow without bound between midnights.
        var file = FileTargetOf(LoadShippedConfigStrictly());

        Assert.Equal(FileArchivePeriod.Day, file.ArchiveEvery);
        Assert.Equal(7, file.MaxArchiveFiles);
        Assert.Equal(TenMegabytes, file.ArchiveAboveSize);
    }

    [Fact]
    public void ShippedConfig_WritesToTheAppDataLogFile()
    {
        var file = FileTargetOf(LoadShippedConfigStrictly());
        var rendered = file.FileName.Render(LogEventInfo.CreateNullEvent());

        Assert.Equal(AppPaths.LogFile(AppLog.FileName), rendered, ignoreCase: true);
    }

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
    public void ShippedConfig_DoesNotUseNLog5sRemovedConcurrentWritesAttribute()
    {
        // Asserted on the text: NLog 6 has no FileTarget.concurrentWrites, so writing it here would
        // parse, do nothing, and still look like a concurrency setting.
        Assert.DoesNotContain("concurrentWrites", SettingsTextOfShippedConfig(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShippedConfig_TimestampStaysGregorianUnderAnyThreadCulture()
    {
        // ${date} defaults to InvariantCulture, but an empty culture= falls back to the thread culture
        // and stamps a non-Gregorian year. ar-SA (Umm al-Qura) tells the two apart; en-GB does not.
        var layout = FileTargetOf(LoadShippedConfigStrictly()).Layout;

        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("ar-SA");
            var rendered = layout.Render(LogEventInfo.Create(LogLevel.Info, "x", "message"));

            Assert.StartsWith($"[{DateTime.Now.Year}-", rendered);
        }
        finally { Thread.CurrentThread.CurrentCulture = original; }
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

    // One log: the power, lid and sleep lines go to app.log with everything else

    [Fact]
    public void ShippedConfig_WritesOneFile_AndPowerEventsReachIt()
    {
        // power.log is gone: a second file target would bring it back, and a power line that reaches
        // no target is a sleep nobody can explain.
        var config = LoadShippedConfigStrictly();

        var file = Assert.Single(config.AllTargets.OfType<FileTarget>());
        Assert.Equal(AppPaths.LogFile(AppLog.FileName), file.FileName.Render(LogEventInfo.CreateNullEvent()),
                     ignoreCase: true);
        Assert.Equal(["appfile"], TargetsFor(config, PowerLog.LoggerName));
        Assert.Equal(["appfile"], TargetsFor(config, AppLog.LoggerName));
    }

    [Fact]
    public void ShippedConfig_TimestampsCarryMillisecondsUnderAnyThreadCulture()
    {
        // Ordering inside one second is what the power lines are read for, so the milliseconds are
        // load-bearing. Rendered under ar-SA for the same reason as the Gregorian-year test.
        var layout = FileTargetOf(LoadShippedConfigStrictly()).Layout;

        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("ar-SA");
            var rendered = layout.Render(LogEventInfo.Create(LogLevel.Info, PowerLog.LoggerName, "message"));

            Assert.Matches($@"^\[{DateTime.Now.Year}-\d{{2}}-\d{{2}} \d{{2}}:\d{{2}}:\d{{2}}\.\d{{3}} [+-]\d{{2}}:\d{{2}}\] INFO\s+\S+\s+message", rendered);
        }
        finally { Thread.CurrentThread.CurrentCulture = original; }
    }

    [Fact]
    public void PowerLog_LineNamesTheEventAndItsCause_InAppLog()
    {
        // A line has to be readable on its own, so it names the event and its cause.
        using var trail = new TempTrail();
        trail.Write((PowerLog.LoggerName, PowerCaller, "Suspending the machine — cause: the lid-close delay elapsed"));

        var line = Assert.Single(File.ReadAllLines(trail.AppFile));
        Assert.Matches(@"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} ", line);
        Assert.Contains("Suspending the machine — cause: the lid-close delay elapsed", line, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(trail.Dir, "power.log")), "power.log must no longer be written.");
    }

    // Rotation, retention and line shape - driven, not parsed

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

    [Fact]
    public void ShippedConfig_ArchiveSettingsAreOnesThisNLogVersionStillHonours()
    {
        // The trap this whole file exists for, in its current form: NLog drops an attribute it does
        // not recognise without a word, so a package bump that renames one of these leaves a config
        // that parses, rotates nothing and deletes nothing. Reflected on the referenced NLog rather
        // than taken from a remembered attribute list, and looked up BY NAME so a removal fails a
        // test instead of failing the compile.
        foreach (var name in new[] { "ArchiveEvery", "MaxArchiveFiles", "ArchiveAboveSize",
                                     "ArchiveSuffixFormat", "LineEnding" })
        {
            var property = typeof(FileTarget).GetProperty(name);
            Assert.True(property is not null,
                $"NLog {typeof(FileTarget).Assembly.GetName().Version} has no FileTarget.{name}, " +
                "which nlog.config relies on. Rotation would silently stop.");
            Assert.True(property!.GetCustomAttribute<ObsoleteAttribute>() is null,
                $"FileTarget.{name} is obsolete in this NLog and nlog.config relies on it.");
        }

        // The two NLog 5 spellings superseded by archiveSuffixFormat, both obsolete in 6.x.
        var settings = SettingsTextOfShippedConfig();
        Assert.DoesNotContain("archiveNumbering", settings, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("archiveDateFormat", settings, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("app.log", "app")]
    public void ShippedConfig_ActuallyRollsToANewFileOnADayBoundary(string fileName, string stem)
    {
        // Driven rather than asserted on attributes: a config can carry archiveEvery and still not
        // roll, because NLog reads the file's birth time rather than the entries in it.
        using var trail = new TempTrail();
        trail.Write((AppLog.LoggerName, AppCaller, "an entry from yesterday"),
                    (PowerLog.LoggerName, PowerCaller, "a power event from yesterday"));

        TempTrail.Age(Path.Combine(trail.Dir, fileName), 1);

        trail.Write((AppLog.LoggerName, AppCaller, "an entry from today"),
                    (PowerLog.LoggerName, PowerCaller, "a power event from today"));

        var archive = trail.Archive(stem, 1);
        Assert.True(File.Exists(archive), $"{fileName} did not roll: no {Path.GetFileName(archive)}.");
        Assert.Contains("yesterday", File.ReadAllText(archive), StringComparison.Ordinal);

        var active = File.ReadAllText(Path.Combine(trail.Dir, fileName));
        Assert.Contains("today", active, StringComparison.Ordinal);
        Assert.DoesNotContain("yesterday", active, StringComparison.Ordinal);
    }

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

    [Fact]
    public void ShippedConfig_DoesNotUseTheAgeBasedRetentionThatDeletesAJustArchivedLog()
    {
        // Asserted on the text as well as the behaviour: re-adding this parses, looks like the
        // policy that was asked for, and silently destroys a log file the moment it is archived.
        Assert.DoesNotContain("maxArchiveDays", SettingsTextOfShippedConfig(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShippedConfig_WritesOneLinePerEntryWithNoBlankLineBetween()
    {
        // Asserted on the bytes and on a line count from a known number of entries, never on the
        // layout text: the layout is exactly what looked correct while writing two line feeds per
        // entry.
        using var trail = new TempTrail();
        trail.Write((AppLog.LoggerName, AppCaller, "one"),
                    (AppLog.LoggerName, AppCaller, "two"),
                    (PowerLog.LoggerName, PowerCaller, "a power event"));

        // Power entries share the one file, in the order they were written.
        foreach (var (path, expectedEntries) in new[] { (trail.AppFile, 3) })
        {
            var bytes = File.ReadAllBytes(path);
            Assert.DoesNotContain((byte)'\r', bytes);
            Assert.Equal(expectedEntries, bytes.Count(b => b == (byte)'\n'));
            Assert.Equal(expectedEntries, File.ReadAllLines(path).Length);
            Assert.DoesNotContain("\n\n", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ShippedConfig_EveryEntryCarriesTheClassItCameFrom()
    {
        // Driven end to end: the class has to survive AppLog.Write, the event property and the layout,
        // and it has to be its own column rather than part of the sentence.
        using var trail = new TempTrail();
        trail.Write((AppLog.LoggerName, AppCaller, "a battery reading was taken"),
                    (PowerLog.LoggerName, PowerCaller, "the lid was closed"));

        var appLines = File.ReadAllLines(trail.AppFile);
        Assert.Matches(@"\] INFO\s+BatteryMonitor\s+a battery reading was taken$", appLines[0]);
        Assert.Matches(@"\] INFO\s+LidDelayPolicy\s+the lid was closed$", appLines[1]);

        // Its own field: splitting the line after the timestamp on runs of whitespace yields the
        // class alone, never glued to the message.
        var fields = System.Text.RegularExpressions.Regex.Split(appLines[0].Split("] ")[1], @"\s{2,}");
        Assert.Equal("INFO", fields[0].Trim());
        Assert.Equal("BatteryMonitor", fields[1].Trim());
    }

    [Fact]
    public void ClassColumn_IsPaddedToTheDeclaredWidth() =>
        // The width is a literal inside the layout string, which no const int can be interpolated into.
        Assert.Contains($"padding=-{AppLog.ClassColumnWidth}", AppLog.ClassColumn, StringComparison.Ordinal);

    [Theory]
    [InlineData(@"X:\src\BatteryMonitor.cs", "BatteryMonitor")]
    [InlineData(@"X:\src\Pages\SettingsPage.xaml.cs", "SettingsPage")]
    [InlineData("/_/Services/AppLog.cs", "AppLog")]
    [InlineData("", "-")]
    public void ClassOf_NamesTheCallersClass(string callerFilePath, string expected) =>
        // CallerFilePath is whatever the compiler recorded, which is a build-machine path on a local
        // build and a mapped one elsewhere; only the file name is used.
        Assert.Equal(expected, AppLog.ClassOf(callerFilePath));
}
