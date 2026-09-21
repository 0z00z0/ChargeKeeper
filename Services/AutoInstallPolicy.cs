namespace ChargeKeeper.Services;

/// <summary>Why an automatic install is not going ahead this tick, or that it is.</summary>
internal enum AutoInstallHold
{
    /// <summary>Nothing is holding it back.</summary>
    None,

    /// <summary>Installing automatically is switched off.</summary>
    SwitchedOff,

    /// <summary>No release has been found to install.</summary>
    NothingToInstall,

    /// <summary>Somebody touched the machine inside <see cref="AutoInstallPolicy.IdleFor"/>, or the
    /// idle reading could not be taken at all.</summary>
    SomebodyIsAtTheMachine,

    /// <summary>A focus session is running.</summary>
    FocusSessionRunning,

    /// <summary>A lid-close wait is running, so the machine is on its way to sleep.</summary>
    LidCloseWaitRunning,
}

/// <summary>
/// Whether a release already found may install itself without asking. The decided behaviour is that
/// it waits until the machine is not in use and never asks, so this is the whole of "not in use".
/// </summary>
/// <remarks>Pure, so each hold is testable without a machine to leave alone.</remarks>
internal static class AutoInstallPolicy
{
    /// <summary>How long nobody may have touched the machine. Long enough that stepping away
    /// mid-sentence does not end in a restart, and short enough that a lunch break is opportunity
    /// enough.</summary>
    internal static readonly TimeSpan IdleFor = TimeSpan.FromMinutes(10);

    /// <summary>The one decision.</summary>
    /// <param name="sinceLastInput">The idle reading, or null where it could not be taken. A
    /// refusal counts as somebody being at the machine: a reading that failed is no evidence that
    /// the machine is free.</param>
    internal static AutoInstallHold Decide(
        bool enabled, bool hasRelease, TimeSpan? sinceLastInput, bool focusRunning, bool lidWaitRunning)
    {
        if (!enabled)      return AutoInstallHold.SwitchedOff;
        if (!hasRelease)   return AutoInstallHold.NothingToInstall;
        if (focusRunning)  return AutoInstallHold.FocusSessionRunning;
        if (lidWaitRunning) return AutoInstallHold.LidCloseWaitRunning;

        return sinceLastInput is { } idle && idle >= IdleFor
            ? AutoInstallHold.None
            : AutoInstallHold.SomebodyIsAtTheMachine;
    }
}
