using System.Runtime.CompilerServices;
using NLog;

namespace ChargeKeeper.Services;

/// <summary>
/// The app's Info/Error log at <c>%AppData%\ChargeKeeper\Logs\app.log</c>, a facade over NLog configured
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
    /// <summary>Spelt again in <c>nlog.config</c>, which cannot read it; <c>NLogConfigTests</c>
    /// asserts the two resolve to the same path.</summary>
    internal const string FileName = "app.log";

    /// <summary>Width of the class column. A name longer than this pushes the message right on that
    /// line rather than being truncated.</summary>
    internal const int ClassColumnWidth = 24;

    /// <summary>The event property carrying the class column's value.</summary>
    internal const string ClassProperty = "class";

    /// <summary>Rendered in the class column when an event carries no class, so an empty column is
    /// visibly empty rather than looking like a formatting fault.</summary>
    internal const string UnknownClass = "-";

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

    // Runs from the _log field initialiser, so anything thrown here escapes as a
    // TypeInitializationException at whichever call site touches AppLog first — several of which are
    // startup and crash paths. It must never throw.
    private static Logger Initialise()
    {
        try
        {
            // Reading LogManager.Configuration triggers NLog's auto-discovery of nlog.config beside
            // the exe. A missing or unparseable file — the latter is a user-editable one, and a bad
            // hand-edit must not be what takes the app down — leaves it null or unset, and NLog then
            // logs nothing at all for any logger this returns. That silence is the whole of the
            // degradation: nothing here builds a second copy of the configuration to fall back to.
            _ = LogManager.Configuration;
        }
        catch { /* left unconfigured; see the remarks above */ }

        return LogManager.GetLogger(LoggerName);
    }
}
