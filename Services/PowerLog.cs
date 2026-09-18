using System.Runtime.CompilerServices;
using NLog;

namespace ChargeKeeper.Services;

/// <summary>
/// The power/sleep trail at <c>%AppData%\ChargeKeeper\Logs\power.log</c>: suspend/resume, the lid, the
/// lid-close delay, keep-awake holds, Smart Standby scheduling and AC↔battery transitions. The
/// nlog.config rule matching <see cref="LoggerName"/> is not <c>final</c>, so every line here also
/// reaches app.log, where it can be correlated with the surrounding startup/teardown chatter.
/// </summary>
internal static class PowerLog
{
    internal const string LoggerName = "ChargeKeeper.Power";

    internal const string FileName = "power.log";

    // Via AppLog so nlog.config has definitely been given the chance to load first; a bare
    // LogManager.GetLogger here can hand back an unconfigured logger.
    private static readonly Logger _log = AppLog.NamedLogger(LoggerName);

    /// <summary>Logs one event: what happened, and what caused it.</summary>
    public static void Event(string what, string cause, [CallerFilePath] string callerFilePath = "") =>
        AppLog.Write(_log, LogLevel.Info, $"{what} — cause: {cause}", callerFilePath);

    /// <summary>
    /// Logs a sentence already written to be read as one — the machine slept and woke, monitoring
    /// started or stopped. No cause clause: these carry their own, and appending one to a
    /// two-sentence line reads as a fragment.
    /// </summary>
    public static void Say(string sentence, [CallerFilePath] string callerFilePath = "") =>
        AppLog.Write(_log, LogLevel.Info, sentence, callerFilePath);
}
