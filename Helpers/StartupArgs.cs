namespace ChargeKeeper.Helpers;

/// <summary>
/// What this process's command line says it is here for. Pure and argv-only — no files, no
/// registry, no <see cref="Environment"/> lookup of its own — so the shape rules below are
/// unit-testable, and so <see cref="Program.Main"/> can branch on them before any of the machinery
/// those branches exist to avoid has loaded.
///
/// <para>Every field is a fact about the LAUNCH, never about state: "was --watchdog-relaunch
/// passed", not "should this probe resurrect the app" (that needs the hold marker and the mutex —
/// see <see cref="Program"/>). Keeping the two apart is what lets the decision be re-read later in
/// startup without re-parsing argv three times, which is what App.OnLaunched used to do.</para>
/// </summary>
/// <param name="IsDebugCommand">The <c>/debug</c> command (see <see cref="CrashDumps"/>) — not a
/// launch at all.</param>
/// <param name="IsWatchdogProbe">A scheduled-task probe (see <see cref="WatchdogTask"/>).</param>
/// <param name="IsAutoRelaunch">The self-heal relaunch spawned by <c>App.OnProcessExit</c>.</param>
internal sealed record StartupArgs(bool IsDebugCommand, bool IsWatchdogProbe, bool IsAutoRelaunch)
{
    /// <summary>Marks the child process that <c>App.OnProcessExit</c> spawns to replace itself.</summary>
    internal const string AutoRelaunchArg = "--auto-relaunch";

    /// <param name="args">A full command line, <see cref="Environment.GetCommandLineArgs"/>-shaped
    /// (element 0 is the exe path and is matched like any other token — it can equal none of these).</param>
    internal static StartupArgs Parse(string[] args) => new(
        IsDebugCommand:  CrashDumps.ParseDebugCommand(args) != CrashDumps.DebugCommand.None,
        IsWatchdogProbe: args.Contains(WatchdogTask.WatchdogArg),
        IsAutoRelaunch:  args.Contains(AutoRelaunchArg));

    /// <summary>
    /// How many times <see cref="SingleInstance.TryAcquireAsync"/> should retry for THIS launch,
    /// at ~200 ms apart.
    ///
    /// <para>The long retry exists for exactly one race: the self-heal relaunch spawns its
    /// replacement while the OLD process may still be milliseconds from terminating and releasing
    /// the mutex, and an instant attempt would read that as "already running" and exit — killing
    /// the tray for good. That relaunch ALWAYS carries <see cref="AutoRelaunchArg"/>, so the wait
    /// belongs to that path alone. Charging every launch 3 seconds of silent nothing to cover it
    /// was pure cost: a user who double-launches the app got no icon, no window and no message for
    /// 3 s before the duplicate quietly exited.</para>
    ///
    /// <para>A plain launch keeps a couple of attempts rather than dropping to one: "Exit from the
    /// tray menu, then immediately start it again" races the dying process's own mutex release the
    /// same way, just with a much shorter fuse.</para>
    ///
    /// <para>A probe gets one instant attempt — finding a live instance is its expected answer, not
    /// a race worth waiting out. (In practice <see cref="Program"/> already resolved the probe
    /// before any of this; the value stays honest regardless.)</para>
    /// </summary>
    internal int SingleInstanceAttempts =>
        IsWatchdogProbe ? 1
        : IsAutoRelaunch ? 15   // ~3 s — the documented self-heal window
        : 3;                    // ~400 ms — covers exit-then-relaunch by hand
}
