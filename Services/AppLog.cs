using System.Runtime.CompilerServices;
using NLog;
using NLog.Config;
using NLog.Targets;
using NLog.Targets.Wrappers;

namespace ChargeKeeper.Services;

/// <summary>
/// The app's Info/Error log at <c>%AppData%\ChargeKeeper\app.log</c>, a facade over NLog configured
/// by <c>nlog.config</c> beside the exe.
/// </summary>
/// <remarks>
/// Several ChargeKeeper processes can hold <c>app.log</c> open at once, so the shipped config pairs
/// <c>keepFileOpen="false"</c> with a <c>RetryingWrapper</c>; without both, concurrent writers lose
/// lines silently. Not <c>concurrentWrites="true"</c> — NLog 6 removed it and ignores it without a
/// word. <c>NLogConfigTests</c> pins this down.
/// </remarks>
internal static class AppLog
{
    /// <summary>Mirrored from <c>nlog.config</c> for <see cref="BuildFallbackConfiguration"/> only —
    /// a fallback cannot read the missing file. <c>NLogConfigTests</c> asserts they match.</summary>
    internal const long ArchiveAboveSizeBytes = 10L * 1024 * 1024;

    /// <summary>Archives kept per trail, one per day. Counted rather than aged: NLog judges
    /// <c>maxArchiveDays</c> by an archive's creation time, which Windows carries over from the log
    /// file it was moved from, so an age rule deletes a long-lived log the moment it is archived.
    /// <c>NLogConfigTests</c> drives both cases.</summary>
    internal const int MaxArchiveFiles = 7;

    /// <summary>Width of the class column. A name longer than this pushes the message right on that
    /// line rather than being truncated.</summary>
    internal const int ClassColumnWidth = 24;

    /// <summary>The event property carrying the class column's value.</summary>
    internal const string ClassProperty = "class";

    /// <summary>Rendered in the class column when an event carries no class, so an empty column is
    /// visibly empty rather than looking like a formatting fault.</summary>
    internal const string UnknownClass = "-";

    /// <summary>
    /// Mirrored from nlog.config, same fallback-only reason as <see cref="ArchiveAboveSizeBytes"/>.
    /// <c>${date}</c> takes no <c>culture=</c>: NLog's default is already invariant, while an EMPTY
    /// <c>culture=</c> falls back to the thread's and stamps e.g. <c>1448-02-03</c> under ar-SA.
    /// The layout carries NO trailing newline: <c>lineEnding="LF"</c> already terminates the entry,
    /// and doing both puts a blank line between entries.
    /// </summary>
    internal const string LineLayout =
        @"[${date:format=yyyy-MM-dd HH\:mm\:ss zzz}] ${level:uppercase=true:padding=-5} " +
        ClassColumn + " ${message}";

    /// <summary>The class column. <c>whenEmpty</c> sits INSIDE the padding wrapper: applied outside
    /// it, the padding makes an empty value non-empty and the placeholder never renders.</summary>
    internal const string ClassColumn =
        "${pad:padding=-24:inner=${event-properties:item=" + ClassProperty +
        ":whenEmpty=" + UnknownClass + "}}";

    internal const string LoggerName = "ChargeKeeper";

    private static readonly Logger _log = Initialise();

    /// <summary>Touching <see cref="_log"/> first forces <see cref="Initialise"/> to have run; a
    /// sibling logger resolved before that gets whatever NLog happened to have.</summary>
    internal static Logger NamedLogger(string name)
    {
        _ = _log.Name;
        return LogManager.GetLogger(name);
    }

    // callerFilePath is supplied by the compiler, so the class column costs nothing at run time and
    // survives async boundaries. ${callsite} would report this facade instead, and measured 2.2x the
    // per-entry cost when made to report the true caller.
    public static void Info(string message, [CallerFilePath] string callerFilePath = "") =>
        Write(_log, LogLevel.Info, message, callerFilePath);

    // The exception is folded into the message rather than passed as NLog's exception argument, so
    // the layout needs no ${exception} clause.
    public static void Error(string source, Exception? ex, [CallerFilePath] string callerFilePath = "") =>
        Write(_log, LogLevel.Error, ex is null ? source : $"{source}\n{ex}", callerFilePath);

    /// <summary>Writes one entry carrying the class column. Shared with <see cref="PowerLog"/> so the
    /// two trails cannot drift apart.</summary>
    internal static void Write(Logger log, LogLevel level, string message, string callerFilePath)
    {
        var entry = LogEventInfo.Create(level, log.Name, message);
        entry.Properties[ClassProperty] = ClassOf(callerFilePath);
        log.Log(entry);
    }

    /// <summary>The class column's value for a caller's source path. File names are unique across the
    /// tree, so the file names its class.</summary>
    internal static string ClassOf(string callerFilePath)
    {
        var name = Path.GetFileNameWithoutExtension(callerFilePath.AsSpan());
        // XAML code-behind arrives as Page.xaml.cs, whose class is Page.
        if (name.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            name = name[..^".xaml".Length];

        return name.IsEmpty ? UnknownClass : name.ToString();
    }

    /// <summary>This file's own path, for entries this class raises about itself.</summary>
    private static string CallerFilePath([CallerFilePath] string path = "") => path;

    // Runs from the _log field initialiser, so anything thrown here escapes as a
    // TypeInitializationException at whichever call site touches AppLog first — several of which are
    // startup and crash paths. It must never throw.
    private static Logger Initialise()
    {
        string? degradedReason = null;

        try
        {
            // Reading LogManager.Configuration triggers NLog's auto-discovery of nlog.config beside
            // the exe. It stays null when the file is absent, and NLog then logs nothing at all,
            // silently — so degrade to a code-built equivalent rather than going dark.
            if (LogManager.Configuration is null)
                degradedReason = "nlog.config was not found beside the exe";
        }
        catch (Exception ex)
        {
            // An unparseable nlog.config: it is a user-editable file, and a bad hand-edit must not be
            // the thing that takes the app down.
            degradedReason = $"nlog.config could not be loaded ({ex.GetType().Name}: {ex.Message})";
        }

        if (degradedReason is null)
            return LogManager.GetLogger(LoggerName);

        try
        {
            LogManager.Configuration = BuildFallbackConfiguration();
            var fallbackLog = LogManager.GetLogger(LoggerName);
            // Without this line a machine on the fallback looks identical to one on the shipped
            // config, and the user's nlog.config edits appear to be ignored for no visible reason.
            Write(fallbackLog, LogLevel.Warn,
                $"{degradedReason} — logging fell back to built-in defaults. Edits to nlog.config " +
                "will have no effect until it is restored/fixed.",
                CallerFilePath());
            return fallbackLog;
        }
        catch
        {
            // Last resort: an unconfigured logger writes nowhere, but it is non-null and never
            // throws, so the call sites keep working.
            return LogManager.GetLogger(LoggerName);
        }
    }

    /// <summary>The code equivalent of the shipped <c>nlog.config</c>, which the tests compare
    /// against the real file.</summary>
    internal static LoggingConfiguration BuildFallbackConfiguration()
    {
        var file = new FileTarget("appfile_file")
        {
            FileName            = AppPaths.DataFile("app.log"),
            Layout              = LineLayout,
            LineEnding          = LineEndingMode.LF,
            KeepFileOpen        = false,   // see the remarks: concurrent processes write this file
            CreateDirs          = true,
            ArchiveAboveSize    = ArchiveAboveSizeBytes,
            ArchiveEvery        = FileArchivePeriod.Day,
            MaxArchiveFiles     = MaxArchiveFiles,
            ArchiveSuffixFormat = "_{1:yyyy-MM-dd}_{0:00}",
            WriteBom            = false,
            Encoding            = System.Text.Encoding.UTF8,
        };

        var retrying = new RetryingTargetWrapper
        {
            Name                   = "appfile",
            WrappedTarget          = file,
            RetryCount             = 5,
            RetryDelayMilliseconds = 20,
        };

        // Same policy, second file — see PowerLog.
        var powerFile = new FileTarget("powerfile_file")
        {
            FileName            = AppPaths.DataFile(PowerLog.FileName),
            Layout              = PowerLog.LineLayout,
            LineEnding          = LineEndingMode.LF,
            KeepFileOpen        = false,
            CreateDirs          = true,
            ArchiveAboveSize    = ArchiveAboveSizeBytes,
            ArchiveEvery        = FileArchivePeriod.Day,
            MaxArchiveFiles     = MaxArchiveFiles,
            ArchiveSuffixFormat = "_{1:yyyy-MM-dd}_{0:00}",
            WriteBom            = false,
            Encoding            = System.Text.Encoding.UTF8,
        };

        var powerRetrying = new RetryingTargetWrapper
        {
            Name                   = "powerfile",
            WrappedTarget          = powerFile,
            RetryCount             = 5,
            RetryDelayMilliseconds = 20,
        };

        var config = new LoggingConfiguration();
        // Power first, and NOT final — the line belongs in both files.
        config.AddRule(LogLevel.Info, LogLevel.Fatal, powerRetrying, PowerLog.LoggerName);
        config.AddRule(LogLevel.Info, LogLevel.Fatal, retrying, "*");
        return config;
    }
}
