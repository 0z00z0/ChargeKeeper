namespace ChargeKeeper.Services;

/// <summary>One movement worth running scripts for: the direction, and the profile it concerns.</summary>
internal readonly record struct NetworkProfileTransition(ScriptTrigger Trigger, string ProfileId);

/// <summary>
/// Which network profile the machine counts as being on, and what moving between profiles runs.
/// Pure — no clock of its own, no settings, no I/O — so what fires is decided in one place and can
/// be shown without docking a machine.
/// </summary>
/// <remarks>
/// Two properties of the readings shape this. The location service reports only the new reading and
/// never reports the one the machine started on, so the previous profile is kept here and starting
/// the application runs nothing. And a failed adapter read during a dock or undock resolves to no
/// network at all, so a lost reading is held for <see cref="SettleWindow"/> before it counts as
/// leaving: coming back to the same profile inside that window is a drop rather than a departure.
/// </remarks>
internal sealed class NetworkProfileTransitions
{
    /// <summary>
    /// How long a lost reading is given to come back before it counts as leaving. The location
    /// service coalesces a burst of network events over 1.5 seconds, so a network cannot be reported
    /// back sooner than that, and the rest of the window is the address a dock has to be given again
    /// before the adapter reads as anything. Long enough to swallow a rebind; short enough that a
    /// cable genuinely pulled out runs its leave while that is still the obvious cause.
    /// </summary>
    public static readonly TimeSpan SettleWindow = TimeSpan.FromSeconds(10);

    private string? _on;          // the profile the machine counts as being on
    private bool    _seeded;      // whether a baseline has been taken at all
    private string? _held;        // a leave held back because the reading was lost
    private DateTimeOffset _heldSince;

    /// <summary>When a held leave stops being a drop and becomes a departure, or null when nothing
    /// is held.</summary>
    public DateTimeOffset? HeldLeaveDueAt => _held is null ? null : _heldSince + SettleWindow;

    /// <summary>The baseline, taken without running anything: the machine is already where it is
    /// when the application starts, and starting is not arriving.</summary>
    public void Seed(string? profileId)
    {
        _on     = profileId;
        _held   = null;
        _seeded = true;
    }

    /// <summary>The baseline moved although the machine did not — a profile was added for the network
    /// it is already on, or the one it was on was deleted. Nothing runs: no reading changed. Ignored
    /// while a leave is held, which is a reading in flight rather than a settled position.</summary>
    public void Rebaseline(string? profileId)
    {
        if (_seeded && _held is null) _on = profileId;
    }

    /// <summary>
    /// One reading: the profile it matches, or null where it matches none.
    /// <paramref name="nothingDetected"/> separates a network that resolved and matches no profile
    /// from no network at all, which is what a failed read during a dock produces.
    /// </summary>
    public IReadOnlyList<NetworkProfileTransition> Observe(
        string? profileId, bool nothingDetected, DateTimeOffset now)
    {
        if (!_seeded)
        {
            Seed(nothingDetected ? null : profileId);
            return [];
        }

        var fired = new List<NetworkProfileTransition>(2);
        fired.AddRange(Expire(now));

        if (_held is { } held)
        {
            if (nothingDetected) return fired;               // still nothing; the window runs from the drop
            if (held == profileId)                            // came back: nothing happened
            {
                _held = null;
                _on   = held;
                return fired;
            }
            fired.Add(new NetworkProfileTransition(ScriptTrigger.NetworkLeft, held));
            _held = null;
            _on   = null;
        }

        if (nothingDetected)
        {
            if (_on is { } dropped)
            {
                _held      = dropped;
                _heldSince = now;
                _on        = null;
            }
            return fired;
        }

        if (profileId == _on) return fired;                   // two readings of one profile is no movement

        if (_on is { } left)        fired.Add(new NetworkProfileTransition(ScriptTrigger.NetworkLeft, left));
        if (profileId is { } joined) fired.Add(new NetworkProfileTransition(ScriptTrigger.NetworkJoined, joined));
        _on = profileId;
        return fired;
    }

    /// <summary>The held leave, once the settle window has run out with nothing coming back.</summary>
    public IReadOnlyList<NetworkProfileTransition> Expire(DateTimeOffset now)
    {
        if (_held is not { } held || now < _heldSince + SettleWindow) return [];
        _held = null;
        return [new NetworkProfileTransition(ScriptTrigger.NetworkLeft, held)];
    }
}
