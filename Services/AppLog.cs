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

    /// <inheritdoc cref="ArchiveAboveSizeBytes"/>
    internal const int MaxArchiveDays = 2;

    /// <summary>
    /// Mirrored from nlog.config, same fallback-only reason as <see cref="ArchiveAboveSizeBytes"/>.
    /// <c>${date}</c> takes no <c>culture=</c>: NLog's default is already invariant, while an EMPTY
    /// <c>culture=</c> falls back to the thread's and stamps e.g. <c>1448-02-03</c> under ar-SA.
    /// </summary>
    internal const string LineLayout =
        @"[${date:format=yyyy-MM-dd HH\:mm\:ss zzz}] ${level:uppercase=true} ${message}" + "\n";

    internal const string LoggerName = "ChargeKeeper";

    private static readonly Logger _log = Initialise();

    /// <summary>Touching <see cref="_log"/> first forces <see cref="Initialise"/> to have run; a
    /// sibling logger resolved before that gets whatever NLog happened to have.</summary>
    internal static Logger NamedLogger(string name)
    {
        _ = _log.Name;
        return LogManager.GetLogger(name);
    }

    public static void Info(string message) => _log.Info(message);

    // The exception is folded into the message rather than passed as NLog's exception argument, so
    // the layout needs no ${exception} clause.
    public static void Error(string source, Exception? ex) =>
        _log.Error(ex is null ? source : $"{source}\n{ex}");

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
            fallbackLog.Warn(
                $"{degradedReason} — logging fell back to built-in defaults. Edits to nlog.config " +
                "will have no effect until it is restored/fixed.");
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
            MaxArchiveDays      = MaxArchiveDays,
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
            MaxArchiveDays      = MaxArchiveDays,
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
