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

    private static RetryingTargetWrapper WrapperOf(LoggingConfiguration config) =>
        (RetryingTargetWrapper)config.FindTargetByName("appfile")!;

    private static FileTarget FileTargetOf(LoggingConfiguration config) =>
        (FileTarget)WrapperOf(config).WrappedTarget!;

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
