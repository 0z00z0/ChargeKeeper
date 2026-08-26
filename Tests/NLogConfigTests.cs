using System.Globalization;
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

    [Fact]
    public void ShippedConfig_ParsesWithNoUnknownOrMisspelledSettings() =>
        // Fails loudly on any attribute this NLog version does not recognise, including one removed
        // by a future major-version bump of the NLog package.
        Assert.NotNull(FileTargetOf(LoadShippedConfigStrictly()));

    [Fact]
    public void ShippedConfig_RotatesAbove10MbAndKeeps2Days()
    {
        // The rotation policy has to live in the config file, not in code.
        var file = FileTargetOf(LoadShippedConfigStrictly());

        Assert.Equal(TenMegabytes, file.ArchiveAboveSize);
        Assert.Equal(2, file.MaxArchiveDays);
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
        var xml = File.ReadAllText(RepoFiles.Find("nlog.config"));
        // The config's own comments discuss concurrentWrites to warn readers off it, so strip them.
        var settings = System.Text.RegularExpressions.Regex.Replace(
            xml, "<!--.*?-->", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);

        Assert.DoesNotContain("concurrentWrites", settings, StringComparison.OrdinalIgnoreCase);
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
        Assert.Equal(2, file.MaxArchiveDays);
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

            Assert.Matches($@"^\[{DateTime.Now.Year}-\d{{2}}-\d{{2}} \d{{2}}:\d{{2}}:\d{{2}}\.\d{{3}}\] message", rendered);
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
