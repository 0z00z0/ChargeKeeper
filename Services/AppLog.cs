using NLog;
using NLog.Config;
using NLog.Targets;
using NLog.Targets.Wrappers;

namespace ChargeKeeper.Services;

/// <summary>
/// Minimal general-purpose event log at <c>%AppData%\ChargeKeeper\app.log</c>. Started life as
/// App's crash-only logger (stowed exceptions bypass Application.UnhandledException, so nothing
/// else could tell what went wrong); extended into a general Info/Error log so major events
/// (history load, prune, time-scale changes) leave a trail too, not just fatal ones.
/// </summary>
/// <remarks>
/// A thin facade over NLog (<c>nlog.config</c> at the repo root, shipped beside the exe). It stays a
/// facade — rather than every call site taking an <c>ILogger</c> — so the ~73 <c>AppLog.Info</c> /
/// <c>AppLog.Error</c> callers are untouched by the swap and the logging back end remains an
/// implementation detail that can be changed again without a 73-file diff.
/// <para>
/// NLog replaced a hand-rolled 106-line writer because the file grew UNBOUNDED — no rotation, and
/// rotation is not worth re-inventing. The size/retention policy deliberately lives in
/// <c>nlog.config</c>, not here, so it can be tuned on an installed machine without a rebuild.
/// </para>
/// <para>
/// The hand-rolled writer's hard-won lesson is preserved in the half that keeps lines: the WRITE
/// path. Its OBSERVABILITY half was deliberately dropped, and that is a real trade, not an
/// oversight — the old writer counted exhausted retries (<c>DroppedWriteCount</c>) and spilled the
/// losing line to a per-process <c>app.log.fallback-{pid}.log</c> "so a genuine failure is
/// observable instead of a black hole". Nothing ever read that counter, and the shipped config
/// measures 0 lines lost where the old one needed the spill — so on the numbers the residual case
/// is rare. But it is not impossible, and when it happens NLog reports it NOWHERE (internalLogLevel
/// is Off). If a line is ever suspected missing again, that is the first thing to re-add. Fix for #34 ("AppLog
/// silently drops lines"): the original wrote via <c>File.AppendAllText</c>, whose default share
/// mode (<see cref="FileShare.Read"/> — deny-write) threw an <see cref="IOException"/> whenever a
/// sibling ChargeKeeper process (the ~288/day watchdog probes, a self-heal relaunch, an
/// OnProcessExit handler racing the startup handoff) held <c>app.log</c> open for write; a bare
/// <c>catch { }</c> then swallowed the line forever. The successor to that
/// <see cref="FileMode.Append"/> + <see cref="FileShare.ReadWrite"/> fix is
/// <c>keepFileOpen="false"</c> in nlog.config, plus a <c>RetryingWrapper</c> standing in for
/// <see cref="Helpers.SafeFileAppend"/>'s bounded retry. NOT <c>concurrentWrites="true"</c> — that
/// was NLog 5's name for it, NLog 6 removed the property, and an unknown attribute is silently
/// ignored, which would have quietly reintroduced #34. Both settings are measured, not assumed:
/// under 6 concurrent processes NLog's defaults lose ~70 lines per 720 and report none of it (not
/// even to its own internal log), while the shipped config loses none. <c>NLogConfigTests</c> pins
/// this down.
/// </para>
/// </remarks>
internal static class AppLog
{
    /// <summary>
    /// Rotation policy, mirrored from <c>nlog.config</c> for the <see cref="BuildFallbackConfiguration"/>
    /// path ONLY — nlog.config remains the source of truth that a user can edit. The duplication is
    /// deliberate (a fallback cannot read the file that is missing) and is kept honest by
    /// <c>NLogConfigTests</c>, which asserts these constants equal the shipped config's values.
    /// </summary>
    internal const long ArchiveAboveSizeBytes = 10L * 1024 * 1024;

    /// <inheritdoc cref="ArchiveAboveSizeBytes"/>
    internal const int MaxArchiveDays = 2;

    /// <summary>
    /// The layout of a log line: <c>[2026-07-16 22:54:09 +02:00] INFO message</c>, then a blank line.
    /// Mirrors nlog.config's <c>layout</c> for the same fallback-only reason as
    /// <see cref="ArchiveAboveSizeBytes"/>, and is asserted equal to it by <c>NLogConfigTests</c>.
    /// <para>
    /// <c>${date}</c> carries NO <c>culture=</c> parameter on purpose: NLog's default for it is
    /// already <see cref="System.Globalization.CultureInfo.InvariantCulture"/>, and this timestamp is
    /// machine-facing log data that must stay Gregorian/ASCII everywhere. Supplying an EMPTY
    /// <c>culture=</c> silently falls back to the thread's culture and stamps e.g. <c>1448-02-03</c>
    /// under <c>ar-SA</c> — the exact bug the #34 review fixed by switching to InvariantCulture.
    /// </para>
    /// </summary>
    internal const string LineLayout =
        @"[${date:format=yyyy-MM-dd HH\:mm\:ss zzz}] ${level:uppercase=true} ${message}" + "\n";

    internal const string LoggerName = "ChargeKeeper";

    private static readonly Logger _log = Initialise();

    public static void Info(string message) => _log.Info(message);

    // The exception is folded into the message text rather than passed as NLog's exception argument
    // so the rendered line is byte-identical to what the hand-rolled writer produced (source, newline,
    // ex.ToString()) and the layout needs no ${exception} clause. Error(source, ex) is the only
    // exception-carrying entry point, so nothing is lost by not giving NLog a typed Exception.
    public static void Error(string source, Exception? ex) =>
        _log.Error(ex is null ? source : $"{source}\n{ex}");

    // Runs from the _log field initialiser, i.e. inside the static constructor: anything thrown here
    // escapes as a TypeInitializationException at whichever of the ~73 call sites happens to touch
    // AppLog first — several of which are startup/crash paths where the old writer's "logging never
    // throws" contract is the only reason the app survives. So this cannot be allowed to throw, and
    // the two failure modes are handled rather than assumed away.
    private static Logger Initialise()
    {
        string? degradedReason = null;

        try
        {
            // Reading LogManager.Configuration is what triggers NLog's auto-discovery of nlog.config
            // beside the exe. It stays null when the file is absent, and NLog then logs NOTHING AT
            // ALL, silently — a silent regression of the app's only forensic trail, precisely the
            // failure mode #34 was about, and precisely the trap Assets\AppIcon.ico fell into twice
            // this week (no CopyToOutputDirectory -> never reached the output -> SetIcon quietly
            // no-op'd). A missing config means a broken install, and the log is how a broken install
            // gets diagnosed, so degrade to an equivalent code-built config instead of going dark.
            // The csproj/installer are still expected to ship the file; NLogConfigTests guards that.
            if (LogManager.Configuration is null)
                degradedReason = "nlog.config was not found beside the exe";
        }
        catch (Exception ex)
        {
            // An unparseable nlog.config (a bad hand-edit — it is a user-editable file by design).
            // NLog's defaults swallow most config errors, but not all of them, and a logging config
            // must never be the thing that takes the app down.
            degradedReason = $"nlog.config could not be loaded ({ex.GetType().Name}: {ex.Message})";
        }

        if (degradedReason is null)
            return LogManager.GetLogger(LoggerName);

        try
        {
            LogManager.Configuration = BuildFallbackConfiguration();
            var fallbackLog = LogManager.GetLogger(LoggerName);
            // Recorded in the trail itself: without this, a machine running on the fallback looks
            // identical to one running the shipped config, and any edit the user made to nlog.config
            // would appear to be ignored for no visible reason.
            fallbackLog.Warn(
                $"{degradedReason} — logging fell back to built-in defaults. Edits to nlog.config " +
                "will have no effect until it is restored/fixed.");
            return fallbackLog;
        }
        catch
        {
            // Everything failed. Hand back a logger with no configuration: it writes nowhere, but it
            // is non-null and never throws, so the call sites keep working. Silence is the last
            // resort here, never the first.
            return LogManager.GetLogger(LoggerName);
        }
    }

    /// <summary>
    /// Builds the code equivalent of the shipped <c>nlog.config</c>. Internal so the tests can compare
    /// it against the real file rather than trusting this to stay in step by inspection.
    /// </summary>
    internal static LoggingConfiguration BuildFallbackConfiguration()
    {
        var file = new FileTarget("appfile_file")
        {
            FileName            = AppPaths.DataFile("app.log"),
            Layout              = LineLayout,
            LineEnding          = LineEndingMode.LF,
            KeepFileOpen        = false,   // see the remarks: successor to the #34 FileShare fix
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

        var config = new LoggingConfiguration();
        config.AddRule(LogLevel.Info, LogLevel.Fatal, retrying, "*");
        return config;
    }
}
