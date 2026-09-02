using System.Runtime.CompilerServices;
using NLog;

namespace ChargeKeeper.Services;

/// <summary>
/// The power/sleep trail at <c>%AppData%\ChargeKeeper\power.log</c>: suspend/resume, the lid, the
/// lid-close delay, keep-awake holds, Smart Standby scheduling and AC↔battery transitions. The
/// nlog.config rule matching <see cref="LoggerName"/> is not <c>final</c>, so every line here also
/// reaches app.log, where it can be correlated with the surrounding startup/teardown chatter.
/// </summary>
internal static class PowerLog
{
    internal const string LoggerName = "ChargeKeeper.Power";

    /// <summary>
    /// Mirrored from nlog.config for the fallback config, and asserted equal to it by
    /// <c>NLogConfigTests</c>. Milliseconds because lid and power events arrive in bursts. No
    /// trailing newline: <c>lineEnding="LF"</c> terminates the entry, and doing both blank-lines it.
    /// </summary>
    internal const string LineLayout =
        @"[${date:format=yyyy-MM-dd HH\:mm\:ss.fff}] " + AppLog.ClassColumn + " ${message}";

    internal const string FileName = "power.log";

    // Via AppLog so nlog.config (or its fallback) has definitely loaded first; a bare
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
