namespace ChargeKeeper.Helpers;

/// <summary>
/// What this process's command line says it is here for. Argv-only, so <see cref="Program.Main"/>
/// can branch on it before any of the machinery those branches exist to avoid has loaded. Every
/// field is a fact about the LAUNCH, never about state.
/// </summary>
internal sealed record StartupArgs(bool IsDebugCommand, bool IsWatchdogProbe)
{
    internal static StartupArgs Parse(string[] args) => new(
        IsDebugCommand:  CrashDumps.ParseDebugCommand(args) != CrashDumps.DebugCommand.None,
        IsWatchdogProbe: args.Contains(TaskDefinitions.WatchdogArg));

    /// <summary>How many times <see cref="SingleInstance.TryAcquireAsync"/> should retry, ~200 ms
    /// apart.</summary>
    internal int SingleInstanceAttempts =>
        IsWatchdogProbe ? 1
        : 3;   // ~400 ms — covers exit-then-relaunch by hand
}
