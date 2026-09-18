using ChargeKeeper.Helpers;

namespace ChargeKeeper.Services;

/// <summary>
/// What sort of sleep this machine does. Recorded once at startup so every later entry in the power
/// trail belongs to a machine of a known kind, and read again by the Lid delay page, which is the one
/// surface whose promise depends on the answer.
/// </summary>
/// <remarks>
/// Read from the OS power capabilities rather than by running <c>powercfg /a</c>: the same in-process
/// call already answers whether the machine has a lid, it costs no process launch, and its answer is
/// a flag rather than localised text that would have to be parsed.
/// </remarks>
internal readonly record struct StandbyCapability(bool ModernStandby, bool SupportsS3)
{
    /// <summary>Asks the platform. Null where the query failed.</summary>
    public static StandbyCapability? Read() =>
        NativeMethods.StandbyFlags() is { } flags
            ? new StandbyCapability(flags.ModernStandby, flags.SupportsS3)
            : null;

    /// <summary>The trail entry for a reading, including the one that could not be taken.</summary>
    public static string Describe(StandbyCapability? capability) => capability switch
    {
        { ModernStandby: true } => "This machine sleeps by Modern Standby (S0 low-power idle)",
        { SupportsS3: true }    => "This machine sleeps by traditional S3 suspend-to-RAM",
        { }                     => "This machine reports neither Modern Standby nor S3 sleep",
        null                    => "This machine's sleep type could not be read from the OS power capabilities",
    };

    /// <summary>
    /// What a lid-close wait can honestly promise on a machine of this kind, or null where the delay
    /// holds and there is nothing to warn about.
    /// </summary>
    /// <remarks>
    /// On a Modern Standby machine the delay's central promise does not always hold. Parking the
    /// Windows lid-close action on "do nothing" stops the lid sleeping the computer, and the hold the
    /// wait takes is the primitive against traditional S3 sleep. On a low-power-idle machine Windows
    /// honours that hold indefinitely on mains but only for a limited time past the sleep timeout on
    /// battery, after which standby can arrive mid-wait. The screen turning off thirty seconds after
    /// the lock taken at lid close is not standby: on mains the application keeps sampling through it.
    /// Saying so is the whole of the remedy: appearing to work is the fault, and a wait that arms,
    /// records its conditions and reports progress while the computer is already asleep is exactly that.
    /// <para>Null for every other reading, the failed one included. A machine whose sleep type could
    /// not be read is not known to have the problem, and a warning on a guess is a worse surface than
    /// none.</para>
    /// </remarks>
    public static string? LidWaitCaveat(StandbyCapability? capability) =>
        capability is { ModernStandby: true }
            ? "This computer sleeps by Modern Standby, where Windows can enter standby on its own "
              + "idle rules while a wait is running. Holding it awake does not reliably prevent that, "
              + "so the computer can sleep sooner than the delay says. The application log states, for each "
              + "wait, how much of it the computer was actually awake for."
            : null;
}
