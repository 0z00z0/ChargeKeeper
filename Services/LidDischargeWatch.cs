namespace ChargeKeeper.Services;

/// <summary>What one battery reading means for an outstanding discharge target.</summary>
internal enum LidDischargeDecision
{
    /// <summary>No target is armed, so the reading decides nothing.</summary>
    NotWatching,
    /// <summary>Above the target and still draining — the machine stays awake.</summary>
    Hold,
    /// <summary>At or below the target — the machine may sleep.</summary>
    TargetReached,
    /// <summary>The pack is taking charge, so the target cannot be reached — the machine may sleep.</summary>
    Charging,
}

/// <summary>
/// Holds a machine awake with the lid shut until its battery has drained to a target charge level.
/// State and rules only — no timer, no power scheme, no settings and no window — so the behaviour is
/// testable without the OS. <see cref="LidDelayService"/> owns the hold and feeds it readings.
/// </summary>
/// <remarks>
/// The stop condition is the charge level, never a "power is connected" reading: connected power may
/// deliver less than the machine draws, so the battery can drain while plugged in. A connectivity
/// test would hold such a machine awake indefinitely, and would release a properly powered one at
/// whatever level it happened to be when the cable came out.
/// </remarks>
internal sealed class LidDischargeWatch
{
    /// <summary>Bounds on the target. 100 % is met the instant a watch arms, and 0 % is a flat
    /// battery; only a hand-edited settings.json lands outside them.</summary>
    public const int MinPercent = 5;
    public const int MaxPercent = 95;

    private readonly System.Threading.Lock _sync = new();

    // Null means nothing is being watched — either never armed, or released by a reading.
    private int? _target;

    /// <summary>The configured target clamped to <see cref="MinPercent"/>…<see cref="MaxPercent"/>.</summary>
    public static int Clamp(int percent) => Math.Clamp(percent, MinPercent, MaxPercent);

    /// <summary>Whether a target is still outstanding.</summary>
    public bool IsWatching { get { lock (_sync) return _target is not null; } }

    /// <summary>The target being watched, or null when none is.</summary>
    public int? Target { get { lock (_sync) return _target; } }

    /// <summary>Starts watching for <paramref name="targetPercent"/>. Re-arming replaces any
    /// outstanding target rather than stacking a second one.</summary>
    public void Arm(int targetPercent)
    {
        lock (_sync) _target = Clamp(targetPercent);
    }

    /// <summary>Abandons the target without deciding anything — the lid reopening, or the feature
    /// being turned off mid-watch.</summary>
    public void Disarm()
    {
        lock (_sync) _target = null;
    }

    /// <summary>
    /// Judges one battery reading. Anything but <see cref="LidDischargeDecision.Hold"/> disarms the
    /// watch, so a release is reported once and a later reading cannot repeat it.
    /// </summary>
    public LidDischargeDecision OnReading(int percent, bool isCharging)
    {
        lock (_sync)
        {
            if (_target is not { } target) return LidDischargeDecision.NotWatching;

            // The level is the stop condition and is read first: a battery already at or under the
            // target is done whichever way it is moving.
            if (percent <= target)
            {
                _target = null;
                return LidDischargeDecision.TargetReached;
            }

            // Charging is a reading of the pack, not of the socket. An underpowered adapter leaves
            // the battery discharging and that machine keeps waiting; a pack actually gaining charge
            // can never come down to a target below it, and a hold waiting for one would never end.
            if (isCharging)
            {
                _target = null;
                return LidDischargeDecision.Charging;
            }

            return LidDischargeDecision.Hold;
        }
    }
}
