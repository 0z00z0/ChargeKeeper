using NLog;

namespace ChargeKeeper.Services;

/// <summary>
/// The power/sleep trail at <c>%AppData%\ChargeKeeper\power.log</c>: suspend/resume, the lid, the
/// lid-close delay, keep-awake holds, Smart Standby scheduling, the startup display settle and
/// AC↔battery transitions. Everything that can answer "why did this machine sleep — or why didn't
/// it", in one file, in order.
/// </summary>
/// <remarks>
/// A named logger, not a namespace: the rule in <c>nlog.config</c> matches
/// <see cref="LoggerName"/>, so a line lands in this file because the call site chose
/// <see cref="PowerLog"/>, never because of which class it happens to sit in.
/// <para>
/// That rule is deliberately NOT <c>final</c>, so every power event ALSO reaches app.log. The trail
/// is a filter over the main log, not a slice taken out of it: App's own comment on
/// <c>PowerModeChanged</c> is that the surrounding startup/teardown chatter is what lets a later
/// silent teardown be correlated with a power event, and moving these lines out would lose exactly
/// that correlation while gaining nothing.
/// </para>
/// <para>
/// Timestamps carry MILLISECONDS, unlike app.log. Ordering inside one second is the whole point
/// here: "lid closed, hold taken, delay armed" is a different story from the same three lines in any
/// other order, and lid/power notifications arrive in bursts.
/// </para>
/// </remarks>
internal static class PowerLog
{
    internal const string LoggerName = "ChargeKeeper.Power";

    /// <summary>
    /// The layout of a power-log line: <c>[2026-08-21 22:54:09.123] Lid closed — cause: lid switch</c>.
    /// Mirrors nlog.config for the same fallback-only reason as <see cref="AppLog.LineLayout"/>, and is
    /// asserted equal to it by <c>NLogConfigTests</c>. No level column — every line here is an event,
    /// not a severity. <c>${date}</c> carries no <c>culture=</c>, for the reason spelled out on
    /// <see cref="AppLog.LineLayout"/>.
    /// </summary>
    internal const string LineLayout =
        @"[${date:format=yyyy-MM-dd HH\:mm\:ss.fff}] ${message}" + "\n";

    internal const string FileName = "power.log";

    // Resolved through AppLog so the nlog.config load (or the fallback it builds) has definitely run
    // first — a bare LogManager.GetLogger here could win the race and hand back an unconfigured logger.
    private static readonly Logger _log = AppLog.NamedLogger(LoggerName);

    /// <summary>
    /// One event: WHAT happened and WHAT CAUSED it. Both halves are required so the file reads on its
    /// own — "suspending" without "the lid-close delay elapsed" is a state, not an explanation, and an
    /// unexplained state is what sends you back to correlating against app.log.
    /// </summary>
    public static void Event(string what, string cause) => _log.Info($"{what} — cause: {cause}");
}
