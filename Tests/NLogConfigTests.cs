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

    /// <summary>
    /// Loads the real nlog.config with <c>throwConfigExceptions</c> forced on. Under NLog's default
    /// a misspelled or stale-version attribute is silently ignored, leaving a config that reads
    /// correctly and does nothing.
    /// </summary>
    private static LoggingConfiguration LoadShippedConfigStrictly()
    {
        var xml = File.ReadAllText(RepoFiles.Find("nlog.config"));
        Assert.Contains("<nlog ", xml);
        return XmlLoggingConfiguration.CreateFromXmlString(
            xml.Replace("<nlog ", "<nlog throwConfigExceptions=\"true\" "));
    }

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
    public void ShippedConfig_RollsDailyKeepsSevenDaysAndStillCapsOneDayAt10Mb()
    {
        // The rotation policy has to live in the config file, not in code. archiveAboveSize stays as
        // a within-day cap: a day's file cannot grow without bound between midnights.
        var file = FileTargetOf(LoadShippedConfigStrictly());

        Assert.Equal(FileArchivePeriod.Day, file.ArchiveEvery);
        Assert.Equal(7, file.MaxArchiveDays);
        Assert.Equal(TenMegabytes, file.ArchiveAboveSize);
    }

    [Fact]
    public void ShippedConfig_WritesToTheAppDataLogFile()
    {
        var file = FileTargetOf(LoadShippedConfigStrictly());
        var rendered = file.FileName.Render(LogEventInfo.CreateNullEvent());

        Assert.Equal(AppPaths.DataFile("app.log"), rendered, ignoreCase: true);
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
            "(watchdog probes, self-heal relaunch) lose their log lines silently. That is #34.");
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

    [Fact]
    public void CodeFallback_MatchesTheShippedConfig()
    {
        // AppLog.BuildFallbackConfiguration duplicates the shipped policy so a missing config degrades
        // to an equivalent logger rather than silence. Duplication drifts unless pinned.
        var shippedConfig  = LoadShippedConfigStrictly();
        var fallbackConfig = AppLog.BuildFallbackConfiguration();
        var shipped  = FileTargetOf(shippedConfig);
        var fallback = FileTargetOf(fallbackConfig);

        Assert.Equal(shipped.ArchiveAboveSize, fallback.ArchiveAboveSize);
        Assert.Equal(shipped.MaxArchiveDays, fallback.MaxArchiveDays);
        Assert.Equal(shipped.ArchiveEvery, fallback.ArchiveEvery);
        Assert.Equal(shipped.KeepFileOpen, fallback.KeepFileOpen);
        Assert.Equal(shipped.LineEnding, fallback.LineEnding);
        Assert.Equal(shipped.ArchiveSuffixFormat, fallback.ArchiveSuffixFormat);
        Assert.Equal(shipped.Layout.ToString(), fallback.Layout.ToString());
        Assert.Equal(shipped.FileName.Render(LogEventInfo.CreateNullEvent()),
                     fallback.FileName.Render(LogEventInfo.CreateNullEvent()), ignoreCase: true);

        // The rest drifts silently: a fallback that wrote a BOM would splice a U+FEFF into the middle
        // of an existing app.log, and the retry policy must not differ between the two paths.
        Assert.Equal(shipped.CreateDirs, fallback.CreateDirs);
        Assert.Equal(shipped.WriteBom, fallback.WriteBom);
        Assert.Equal(shipped.Encoding, fallback.Encoding);
        Assert.Equal(Rendered(WrapperOf(shippedConfig).RetryCount),
                     Rendered(WrapperOf(fallbackConfig).RetryCount));
        Assert.Equal(Rendered(WrapperOf(shippedConfig).RetryDelayMilliseconds),
                     Rendered(WrapperOf(fallbackConfig).RetryDelayMilliseconds));
    }

    // Power trail (power.log)

    [Fact]
    public void ShippedConfig_RoutesPowerEventsToTheirOwnFile()
    {
        // The point of the target: "why did this machine sleep" is answered from one file.
        var config = LoadShippedConfigStrictly();

        Assert.Contains("powerfile", TargetsFor(config, PowerLog.LoggerName));
        Assert.Equal(AppPaths.DataFile(PowerLog.FileName),
                     FileTargetOf(config, "powerfile").FileName.Render(LogEventInfo.CreateNullEvent()),
                     ignoreCase: true);
    }

    [Fact]
    public void ShippedConfig_PowerEventsAlsoReachAppLog()
    {
        // The power rule is deliberately not final: power.log is a filter over the trail, not a slice
        // taken out of it. A final="true" would also strand every logger declared after it, so the
        // rule itself is asserted rather than inferred from the target list.
        var config = LoadShippedConfigStrictly();

        var powerRule = Assert.Single(config.LoggingRules, r => r.LoggerNamePattern == PowerLog.LoggerName);
        Assert.False(powerRule.Final,
            "the ChargeKeeper.Power rule must not be final — power events belong in app.log too.");
        Assert.Contains("appfile", TargetsFor(config, PowerLog.LoggerName));
    }

    [Fact]
    public void ShippedConfig_OrdinaryLoggersDoNotReachThePowerFile()
    {
        // A line lands in power.log because the call site chose PowerLog, never because of its namespace.
        Assert.DoesNotContain("powerfile", TargetsFor(LoadShippedConfigStrictly(), AppLog.LoggerName));
    }

    [Fact]
    public void ShippedConfig_PowerFileRotatesAndIsConcurrentWriterSafeLikeAppLog()
    {
        // Sibling ChargeKeeper processes append here too, so the same rotation and retry policy applies.
        var config = LoadShippedConfigStrictly();
        var file   = FileTargetOf(config, "powerfile");

        Assert.Equal(TenMegabytes, file.ArchiveAboveSize);
        Assert.Equal(FileArchivePeriod.Day, file.ArchiveEvery);
        Assert.Equal(7, file.MaxArchiveDays);
        Assert.False(file.KeepFileOpen);
        Assert.Equal("5",  Rendered(WrapperOf(config, "powerfile").RetryCount));
        Assert.Equal("20", Rendered(WrapperOf(config, "powerfile").RetryDelayMilliseconds));
    }

    [Fact]
    public void ShippedConfig_PowerTimestampsAreIsoWithMillisecondsUnderAnyThreadCulture()
    {
        // Ordering inside one second is what this file is for, so the milliseconds are load-bearing.
        // Rendered under ar-SA for the same reason as app.log's layout.
        var layout = FileTargetOf(LoadShippedConfigStrictly(), "powerfile").Layout;

        var original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("ar-SA");
            var rendered = layout.Render(LogEventInfo.Create(LogLevel.Info, PowerLog.LoggerName, "message"));

            Assert.Matches($@"^\[{DateTime.Now.Year}-\d{{2}}-\d{{2}} \d{{2}}:\d{{2}}:\d{{2}}\.\d{{3}}\] \S+\s+message", rendered);
        }
        finally { Thread.CurrentThread.CurrentCulture = original; }
    }

    [Fact]
    public void CodeFallback_CarriesThePowerFileToo()
    {
        // A missing nlog.config is exactly when someone is troubleshooting sleep, so the degraded
        // config keeps the split rather than collapsing everything back into app.log.
        var shippedConfig  = LoadShippedConfigStrictly();
        var fallbackConfig = AppLog.BuildFallbackConfiguration();
        var shipped  = FileTargetOf(shippedConfig, "powerfile");
        var fallback = FileTargetOf(fallbackConfig, "powerfile");

        Assert.Equal(shipped.ArchiveAboveSize, fallback.ArchiveAboveSize);
        Assert.Equal(shipped.MaxArchiveDays, fallback.MaxArchiveDays);
        Assert.Equal(shipped.ArchiveEvery, fallback.ArchiveEvery);
        Assert.Equal(shipped.KeepFileOpen, fallback.KeepFileOpen);
        Assert.Equal(shipped.LineEnding, fallback.LineEnding);
        Assert.Equal(shipped.ArchiveSuffixFormat, fallback.ArchiveSuffixFormat);
        Assert.Equal(shipped.CreateDirs, fallback.CreateDirs);
        Assert.Equal(shipped.WriteBom, fallback.WriteBom);
        Assert.Equal(shipped.Encoding, fallback.Encoding);
        Assert.Equal(shipped.Layout.ToString(), fallback.Layout.ToString());
        Assert.Equal(shipped.FileName.Render(LogEventInfo.CreateNullEvent()),
                     fallback.FileName.Render(LogEventInfo.CreateNullEvent()), ignoreCase: true);

        // And routes the same way — including the "also reaches app.log" half.
        Assert.Contains("powerfile", TargetsFor(fallbackConfig, PowerLog.LoggerName));
        Assert.Contains("appfile", TargetsFor(fallbackConfig, PowerLog.LoggerName));
        Assert.DoesNotContain("powerfile", TargetsFor(fallbackConfig, AppLog.LoggerName));
    }

    [Fact]
    public void PowerLog_LineNamesTheEventAndItsCause()
    {
        // The file's contract: a line has to be readable on its own, so it names the event and its
        // cause. An unexplained state sends the reader back to correlating against app.log.
        var config = AppLog.BuildFallbackConfiguration();
        var file   = FileTargetOf(config, "powerfile");
        var dir    = Path.Combine(Path.GetTempPath(), $"ck-powerlog-test-{Guid.NewGuid():N}");
        file.FileName = Path.Combine(dir, PowerLog.FileName);
        // Redirected too: an un-redirected app.log target would write to the real user's log.
        FileTargetOf(config).FileName = Path.Combine(dir, "app.log");

        try
        {
            var factory = new LogFactory { Configuration = config };
            factory.GetLogger(PowerLog.LoggerName).Info("Suspending the machine — cause: the lid-close delay elapsed");
            factory.Flush();

            var line = File.ReadAllText(file.FileName.Render(LogEventInfo.CreateNullEvent()));

            Assert.Matches(@"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\] ", line);
            Assert.Contains("Suspending the machine — cause: the lid-close delay elapsed", line);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    // Rotation, retention and line shape - driven, not parsed

    /// <summary>
    /// A throwaway copy of both trails. Every write goes through a freshly loaded copy of the shipped
    /// config with BOTH file names redirected here, so nothing reaches the real per-user log
    /// directory, and each call re-probes the file's age exactly as a restarted process would.
    /// </summary>
    private sealed class TempTrail : IDisposable
    {
        public string Dir { get; } =
            Path.Combine(Path.GetTempPath(), $"ck-nlogconfig-{Guid.NewGuid():N}");

        public string AppFile => Path.Combine(Dir, "app.log");
        public string PowerFile => Path.Combine(Dir, PowerLog.FileName);

        public TempTrail() => Directory.CreateDirectory(Dir);

        /// <summary>Writes through <see cref="AppLog.Write"/>, the one writer both trails share.</summary>
        public void Write(params (string Logger, string CallerFile, string Message)[] entries)
        {
            var config = LoadShippedConfigStrictly();
            FileTargetOf(config).FileName = AppFile;
            FileTargetOf(config, "powerfile").FileName = PowerFile;
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
        foreach (var name in new[] { "ArchiveEvery", "MaxArchiveDays", "ArchiveAboveSize",
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
    [InlineData("power.log", "power")]
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
    [InlineData("power")]
    public void ShippedConfig_ActuallyDeletesArchivesOlderThanSevenDays(string stem)
    {
        // Deletion is the half that fails silently: nothing in the app notices archives piling up.
        using var trail = new TempTrail();
        foreach (var age in new[] { 6, 7, 8, 30 })
            trail.PlantArchive(stem, age);

        // The trails share their directory with the settings file and the battery history, so the
        // sweep must reach archives of this trail and nothing else.
        var bystander = Path.Combine(trail.Dir, "settings.json");
        File.WriteAllText(bystander, "{}");
        TempTrail.Age(bystander, 400);

        trail.Write((AppLog.LoggerName, AppCaller, "an entry"),
                    (PowerLog.LoggerName, PowerCaller, "a power event"));

        Assert.True(File.Exists(bystander), "the sweep must not delete files that are not its archives.");
        Assert.True(File.Exists(trail.Archive(stem, 6)), "a 6-day-old archive must survive.");
        Assert.True(File.Exists(trail.Archive(stem, 7)), "a 7-day-old archive must survive.");
        Assert.False(File.Exists(trail.Archive(stem, 8)), "an 8-day-old archive must be deleted.");
        Assert.False(File.Exists(trail.Archive(stem, 30)), "a 30-day-old archive must be deleted.");
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

        // The power rule is not final, so that third entry lands in both files.
        foreach (var (path, expectedEntries) in new[] { (trail.AppFile, 3), (trail.PowerFile, 1) })
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

        var powerLine = Assert.Single(File.ReadAllLines(trail.PowerFile));
        Assert.Matches(@"\] LidDelayPolicy\s+the lid was closed$", powerLine);

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

    [Fact]
    public void CodeFallback_ConstantsMatchTheShippedConfig()
    {
        // Guards the constants AppLog exposes against the real file.
        var shipped = FileTargetOf(LoadShippedConfigStrictly());

        Assert.Equal(AppLog.ArchiveAboveSizeBytes, shipped.ArchiveAboveSize);
        Assert.Equal(AppLog.MaxArchiveDays, shipped.MaxArchiveDays);
        Assert.Equal(TenMegabytes, AppLog.ArchiveAboveSizeBytes);
    }
}
