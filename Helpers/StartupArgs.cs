namespace ChargeKeeper.Helpers;

/// <summary>
/// What this process's command line says it is here for. Argv-only, so <see cref="Program.Main"/>
/// can branch on it before any of the machinery those branches exist to avoid has loaded. Every
/// field is a fact about the LAUNCH, never about state.
/// </summary>
internal sealed record StartupArgs(bool IsDebugCommand, bool IsWatchdogProbe, bool IsAutoRelaunch)
{
    /// <summary>Marks the child process that <c>App.OnProcessExit</c> spawns to replace itself.</summary>
    internal const string AutoRelaunchArg = "--auto-relaunch";

    internal static StartupArgs Parse(string[] args) => new(
        IsDebugCommand:  CrashDumps.ParseDebugCommand(args) != CrashDumps.DebugCommand.None,
        IsWatchdogProbe: args.Contains(TaskDefinitions.WatchdogArg),
        IsAutoRelaunch:  args.Contains(AutoRelaunchArg));

    /// <summary>How many times <see cref="SingleInstance.TryAcquireAsync"/> should retry, ~200 ms
    /// apart. The long retry belongs to the self-heal relaunch alone: it spawns its replacement while
    /// the old process may still be milliseconds from releasing the mutex, and an instant attempt
    /// would read that as "already running" and kill the tray for good.</summary>
    internal int SingleInstanceAttempts =>
        IsWatchdogProbe ? 1
        : IsAutoRelaunch ? 15   // ~3 s — the documented self-heal window
        : 3;                    // ~400 ms — covers exit-then-relaunch by hand
}
