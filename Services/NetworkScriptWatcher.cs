namespace ChargeKeeper.Services;

/// <summary>
/// Runs the scripts bound to joining and leaving a named network profile. The decision of what a
/// reading means is <see cref="NetworkProfileTransitions"/>; this holds the subscriptions, the
/// clock and the settle timer, so the decision stays testable without a network.
/// </summary>
internal sealed class NetworkScriptWatcher
{
    /// <summary>The watcher the application uses. A second instance exists only in a test.</summary>
    public static NetworkScriptWatcher Instance { get; } = new(ScriptRunner.Instance);

    private readonly ScriptRunner _runner;
    private readonly NetworkProfileTransitions _transitions = new();
    private readonly Lock _gate = new();
    private System.Threading.Timer? _settleTimer;
    private bool _started;

    internal NetworkScriptWatcher(ScriptRunner runner) => _runner = runner;

    /// <summary>Wires the reactions, once, at startup. The baseline arrives with the location
    /// service's first evaluation and runs nothing.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
        }

        NetworkLocationService.LocationSeeded  += OnSeeded;
        NetworkLocationService.LocationChanged += OnLocationChanged;
        // A profile added for the network the machine is already on, or the one it was on deleted,
        // moves what the next reading is measured against without the machine having moved.
        SettingsService.ChangeCommitted += _ => Rebaseline();
        SettingsService.Reloaded        += Rebaseline;
    }

    private void OnSeeded(NetworkLocation location)
    {
        lock (_gate) _transitions.Seed(ProfileIdAt(location));
    }

    private void OnLocationChanged(NetworkLocation location)
    {
        IReadOnlyList<NetworkProfileTransition> fired;
        lock (_gate)
        {
            fired = _transitions.Observe(ProfileIdAt(location), location.IsEmpty, DateTimeOffset.Now);
            ArmSettleTimer();
        }
        Run(fired);
    }

    private void Rebaseline()
    {
        lock (_gate) _transitions.Rebaseline(ProfileIdAt(NetworkLocationService.LastKnown));
    }

    // Caller holds the gate. One timer for the watcher's lifetime, re-armed per held leave.
    private void ArmSettleTimer()
    {
        if (_transitions.HeldLeaveDueAt is not { } due)
        {
            _settleTimer?.Change(System.Threading.Timeout.InfiniteTimeSpan,
                                 System.Threading.Timeout.InfiniteTimeSpan);
            return;
        }

        var wait = due - DateTimeOffset.Now;
        if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;

        _settleTimer ??= new System.Threading.Timer(_ => OnSettleElapsed(), null,
                                                    System.Threading.Timeout.InfiniteTimeSpan,
                                                    System.Threading.Timeout.InfiniteTimeSpan);
        _settleTimer.Change(wait, System.Threading.Timeout.InfiniteTimeSpan);
    }

    private void OnSettleElapsed()
    {
        IReadOnlyList<NetworkProfileTransition> fired;
        lock (_gate) fired = _transitions.Expire(DateTimeOffset.Now);
        Run(fired);
    }

    private static string? ProfileIdAt(NetworkLocation location) =>
        SettingsService.Read(s => s.FindNetworkRule(location)?.Id);

    /// <summary>Runs what the transition asks for. A profile deleted since the machine arrived runs
    /// nothing — a script bound to it says on its own row that the profile is gone.</summary>
    private void Run(IReadOnlyList<NetworkProfileTransition> fired)
    {
        foreach (var transition in fired)
        {
            string? name = SettingsService.Read(
                s => s.NetworkLocationRules.FirstOrDefault(r => r.Id == transition.ProfileId)?.Name);
            if (name is null) continue;

            _runner.Fire(transition.Trigger, transition.ProfileId, name);
        }
    }
}
