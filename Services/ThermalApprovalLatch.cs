namespace ChargeKeeper.Services;

/// <summary>
/// Whether a temperature reading has ever been approved by <see cref="ThermalReadingGate"/> during
/// this run, per issue #205. Pure state — fed one gate outcome at a time by
/// <see cref="ThermalStatusService"/> — kept separate from the hardware read so the one-shot
/// behaviour is provable without a thermal zone, the same shape <see cref="ThermalReadingGate"/> and
/// <see cref="LidThermalWatch"/> use.
/// </summary>
/// <remarks>
/// A stretch of withheld readings under steady load — the gate wants movement across several samples
/// — is not the same claim as "this machine has no trustworthy reading". Once true this never goes
/// back to false: a card or a safeguard deciding whether to offer the feature at all reads this
/// rather than the moment-to-moment value, which can and does go quiet for minutes at a time on a
/// healthy machine.
/// </remarks>
internal sealed class ThermalApprovalLatch
{
    private readonly System.Threading.Lock _sync = new();
    private bool _hasEverApproved;

    /// <summary>Whether an approved reading has ever been observed.</summary>
    public bool HasEverApproved { get { lock (_sync) return _hasEverApproved; } }

    /// <summary>
    /// Records one gate outcome. Returns true exactly once — the call on which the latch first
    /// becomes true — so the caller knows precisely when to raise a one-shot event rather than
    /// re-deriving it from a before/after comparison.
    /// </summary>
    public bool Observe(bool approved)
    {
        lock (_sync)
        {
            if (!approved || _hasEverApproved) return false;
            _hasEverApproved = true;
            return true;
        }
    }
}
