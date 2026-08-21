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
/// Guards the SHIPPED nlog.config. Every assertion here exists because the failure it catches is
/// SILENT: NLog ignores an unknown attribute, logs nothing at all when the config is missing, and
/// reports a dropped write nowhere — so a broken logging config looks exactly like a quiet app.
/// app.log is the app's only forensic trail, and #34 was a whole issue about it silently dropping
/// lines, so "it built" is not evidence that it logs.
/// </summary>
public class NLogConfigTests
{
    private const long TenMegabytes = 10L * 1024 * 1024;

    /// <summary>
    /// Locates nlog.config the same way AboutCreditsTests locates the README: by probing upwards for
    /// the repo marker rather than hard-coding the test output's depth.
    /// </summary>
    private static string FindRepoFile(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, name);
            if (File.Exists(candidate) && File.Exists(Path.Combine(dir.FullName, "ChargeKeeper.csproj")))
                return candidate;
        }

        throw new FileNotFoundException($"Could not locate '{name}' walking up from '{AppContext.BaseDirectory}'.");
    }

    /// <summary>
    /// Loads the real nlog.config with <c>throwConfigExceptions</c> forced ON. This is the crux: with
    /// NLog's default (off) a misspelled or stale-version attribute is SILENTLY IGNORED, leaving a
    /// config that reads correctly and does nothing. Notably <c>concurrentWrites="true"</c> — NLog
    /// 5's spelling of the concurrency fix — parses fine and is discarded by NLog 6.
    /// </summary>
    private static LoggingConfiguration LoadShippedConfigStrictly()
    {
        var xml = File.ReadAllText(FindRepoFile("nlog.config"));
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
        // Fails loudly on any attribute this NLog version does not recognise, incl. one removed by a
        // future major-version bump of the NLog PackageReference.
        Assert.NotNull(FileTargetOf(LoadShippedConfigStrictly()));

    [Fact]
    public void ShippedConfig_RotatesAbove10MbAndKeeps2Days()
    {
        // The user's explicit requirement, and the reason NLog was adopted at all: app.log grew
        // unbounded. These numbers must live in the CONFIG, not in code — asserted against the file.
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
        // The #34 fix, in its NLog spelling. keepFileOpen="false" (open-per-write, share-tolerant) is
        // the successor to FileMode.Append + FileShare.ReadWrite; the RetryingWrapper is the successor
        // to SafeFileAppend's bounded retry. Measured: NLog's keepFileOpen="true" DEFAULT loses ~70
        // lines per 720 across 6 concurrent processes, silently. Neither setting is decoration.
        var config = LoadShippedConfigStrictly();

        var wrapper = Assert.IsType<RetryingTargetWrapper>(config.FindTargetByName("appfile"));

        // Asserted as EXACT values, not merely "not zero": NLog's own defaults (3 x 100ms) are
        // non-zero too, so a config that lost both attributes entirely would sail through a
        // not-zero check while quietly retrying to a different policy than the fallback in code.
        Assert.Equal("5",  Rendered(wrapper.RetryCount));
        Assert.Equal("20", Rendered(wrapper.RetryDelayMilliseconds));
        Assert.False(FileTargetOf(config).KeepFileOpen,
            "keepFileOpen must stay false — an exclusive handle makes sibling ChargeKeeper processes " +
            "(watchdog probes, self-heal relaunch) lose their log lines silently. That is #34.");
    }

    [Fact]
    public void ShippedConfig_DoesNotUseNLog5sRemovedConcurrentWritesAttribute()
    {
        // Deliberately asserted on the TEXT: NLog 6 removed FileTarget.concurrentWrites, so writing it
        // here would parse (by default), do nothing, and reintroduce #34 while looking like the fix.
        // The strict-parse test above would also catch it — this one names the specific trap so the
        // failure message explains itself to whoever reaches for the NLog 5 docs.
        var xml = File.ReadAllText(FindRepoFile("nlog.config"));
        // Comments are stripped first: they discuss concurrentWrites at length precisely to stop the
        // next reader from adding it, and must not trip the test they exist to explain.
        var settings = System.Text.RegularExpressions.Regex.Replace(
            xml, "<!--.*?-->", string.Empty, System.Text.RegularExpressions.RegexOptions.Singleline);

        Assert.DoesNotContain("concurrentWrites", settings, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShippedConfig_TimestampStaysGregorianUnderAnyThreadCulture()
    {
        // The #34-review lesson, made executable. The layout omits culture= because NLog's ${date}
        // already defaults to InvariantCulture; adding an EMPTY culture= silently falls back to the
        // thread culture and stamps a non-Gregorian year. Rendering under ar-SA (Umm al-Qura) is what
        // tells the two apart — under en-GB both look identical, which is how it would sneak back in.
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
        // NLog discovers nlog.config BESIDE THE EXE. If the csproj ever loses the
        // CopyToOutputDirectory metadata, the file stays in the repo, NLog finds no config and logs
        // nothing — with no error. This repo has shipped exactly that bug twice (Assets\AppIcon.ico
        // without CopyToOutputDirectory -> SetIcon silently no-op'd), so it gets a test rather than a
        // comment. This assembly's output is fed by ChargeKeeper.csproj's Content item via the
        // ProjectReference, so its presence here is evidence the item is doing its job.
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "nlog.config")),
            $"nlog.config is missing from the build output ({AppContext.BaseDirectory}). Check the " +
            "Content item + CopyToOutputDirectory in ChargeKeeper.csproj — NLog would silently log nothing.");
    }

    [Fact]
    public void CodeFallback_MatchesTheShippedConfig()
    {
        // AppLog.BuildFallbackConfiguration duplicates the shipped policy so a missing config degrades
        // to an equivalent logger rather than silence. Duplication drifts unless pinned — so pin it,
        // the same way AboutCreditsTests pins the README against the About box.
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

        // Every remaining setting BuildFallbackConfiguration bothers to state. Each was unpinned
        // while the doc comment claimed this test kept the duplication honest, and each drifts
        // silently: a fallback that wrote a BOM would splice a U+FEFF into the middle of an existing
        // app.log, and the retry policy is the #34 fix itself — the one thing that must not differ
        // between the two paths.
        Assert.Equal(shipped.CreateDirs, fallback.CreateDirs);
        Assert.Equal(shipped.WriteBom, fallback.WriteBom);
        Assert.Equal(shipped.Encoding, fallback.Encoding);
        Assert.Equal(Rendered(WrapperOf(shippedConfig).RetryCount),
                     Rendered(WrapperOf(fallbackConfig).RetryCount));
        Assert.Equal(Rendered(WrapperOf(shippedConfig).RetryDelayMilliseconds),
                     Rendered(WrapperOf(fallbackConfig).RetryDelayMilliseconds));
    }

    // ── Power trail (power.log) ────────────────────────────────────────────────────

    [Fact]
    public void ShippedConfig_RoutesPowerEventsToTheirOwnFile()
    {
        // The point of the whole target: a "why did this machine sleep" question is answered from one
        // file instead of by eye over app.log.
        var config = LoadShippedConfigStrictly();

        Assert.Contains("powerfile", TargetsFor(config, PowerLog.LoggerName));
        Assert.Equal(AppPaths.DataFile(PowerLog.FileName),
                     FileTargetOf(config, "powerfile").FileName.Render(LogEventInfo.CreateNullEvent()),
                     ignoreCase: true);
    }

    [Fact]
    public void ShippedConfig_PowerEventsAlsoReachAppLog()
    {
        // The power rule is deliberately NOT final: the file is a FILTER over the trail, not a slice
        // taken out of it, because a power event is read against the startup/teardown chatter around
        // it. A stray final="true" would silently move these lines instead of copying them — and would
        // ALSO strand every logger declared after the power rule, so it is asserted on the rule itself
        // and not merely inferred from the target list.
        var config = LoadShippedConfigStrictly();

        var powerRule = Assert.Single(config.LoggingRules, r => r.LoggerNamePattern == PowerLog.LoggerName);
        Assert.False(powerRule.Final,
            "the ChargeKeeper.Power rule must not be final — power events belong in app.log too.");
        Assert.Contains("appfile", TargetsFor(config, PowerLog.LoggerName));
    }

    [Fact]
    public void ShippedConfig_OrdinaryLoggersDoNotReachThePowerFile()
    {
        // The other half of "explicit routing": a line lands in power.log because the call site chose
        // PowerLog, never because of the namespace it happens to sit in.
        Assert.DoesNotContain("powerfile", TargetsFor(LoadShippedConfigStrictly(), AppLog.LoggerName));
    }

    [Fact]
    public void ShippedConfig_PowerFileRotatesAndIsConcurrentWriterSafeLikeAppLog()
    {
        // Same #34 story, same rotation policy — sibling ChargeKeeper processes append here too.
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
        // Ordering inside one second is what this file is for, so the milliseconds are load-bearing,
        // not decoration. Rendered under ar-SA for the same reason as app.log's layout: an empty
        // culture= would stamp a non-Gregorian year and the two look identical under en-GB.
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
        // config keeps the split rather than collapsing everything back into app.log. Pinned against
        // the shipped file for the same reason CodeFallback_MatchesTheShippedConfig is.
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
        // The file's whole contract: a line has to be readable on its own. "Suspending the machine"
        // without "the lid-close delay elapsed" is a state, not an explanation — and an unexplained
        // state sends you straight back to correlating against app.log, which is what this avoids.
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
        // Guards the constants AppLog exposes (and its doc comments quote) against the real file.
        var shipped = FileTargetOf(LoadShippedConfigStrictly());

        Assert.Equal(shipped.ArchiveAboveSize, AppLog.ArchiveAboveSizeBytes);
        Assert.Equal(shipped.MaxArchiveDays, AppLog.MaxArchiveDays);
        Assert.Equal(TenMegabytes, AppLog.ArchiveAboveSizeBytes);
    }
}
