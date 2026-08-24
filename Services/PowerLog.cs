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
    /// <c>NLogConfigTests</c>. Milliseconds because lid and power events arrive in bursts.
    /// </summary>
    internal const string LineLayout =
        @"[${date:format=yyyy-MM-dd HH\:mm\:ss.fff}] ${message}" + "\n";

    internal const string FileName = "power.log";

    // Via AppLog so nlog.config (or its fallback) has definitely loaded first; a bare
    // LogManager.GetLogger here can hand back an unconfigured logger.
    private static readonly Logger _log = AppLog.NamedLogger(LoggerName);

    /// <summary>Logs one event: what happened, and what caused it.</summary>
    public static void Event(string what, string cause) => _log.Info($"{what} — cause: {cause}");
}
