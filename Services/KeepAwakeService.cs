using System.Collections.Concurrent;
using ChargeKeeper.Helpers;

namespace ChargeKeeper.Services;

/// <summary>
/// Holds the machine awake for a bounded session. The clock rules live in the pure
/// <see cref="KeepAwakePolicy"/>; this owns the OS hold, the expiry timer and the network reactions.
/// </summary>
/// <remarks>
/// <c>SetThreadExecutionState</c> is per-thread — the request dies with the thread that made it — so
/// the hold lives on one dedicated background thread fed by a request queue, and releasing means
/// posting <c>ES_CONTINUOUS</c> alone to that same thread. The active session is not persisted:
/// keep-awake surviving a reboot would be a surprise, so only the presets and the display-on
/// preference are settings.
/// </remarks>
internal static class KeepAwakeService
{
    // Guards _current + _expiryTimer + the holder-thread start. StateChanged is raised outside the
    // lock so a slow subscriber (a tray/tooltip rebuild) can't stall an expiry or a location change.
    private static readonly System.Threading.Lock _sync = new();
    private static KeepAwakeSession? _current;
    private static System.Threading.Timer? _expiryTimer;

    // The holder thread and its request queue; each item is the esFlags value to apply.
    private static readonly BlockingCollection<uint> _holdRequests = new();
    private static Thread? _holder;
    // The flags last posted, so a settings change that leaves them alone costs nothing.
    private static uint _postedFlags;

    private static bool _started;

    /// <summary>Raised (off the UI thread) whenever a session starts, ends, or expires.</summary>
    public static event Action? StateChanged;

    /// <summary>The running session, or null when nothing is holding the machine awake.</summary>
    public static KeepAwakeSession? Current { get { lock (_sync) return _current; } }

    /// <summary>Wires the network-location reactions and the settings reaction. Called once at
    /// startup; never unsubscribed, since the subscriptions live for the whole process.</summary>
    public static void Start()
    {
        lock (_sync)
        {
            if (_started) return;
            _started = true;
        }
        NetworkLocationService.LocationChanged += OnLocationChanged;
        // Every writer of the display preference — Settings, MQTT, the dashboard — goes through
        // SettingsService, so one subscription here reaches all of them.
        SettingsService.ChangeCommitted += _ => ReapplyHold();
    }

    /// <summary>
    /// Re-posts the OS hold when <see cref="AppSettings.KeepAwakeDisplayOn"/> has moved under a
    /// running session, so the screen choice takes effect now rather than on the next activation.
    /// No-op when nothing is running or the flags are unchanged.
    /// </summary>
    private static void ReapplyHold()
    {
        lock (_sync)
        {
            if (_current is null || _holder is null) return;
            uint flags = HoldFlags();
            if (flags == _postedFlags) return;
            PostLocked(flags);
        }
        RaiseStateChanged();
    }

    /// <summary>Starts or replaces the session, applying the OS hold for the current
    /// <see cref="AppSettings.KeepAwakeDisplayOn"/>.</summary>
    /// <param name="cause">Who asked, for the power trail. Defaults to the user because every entry
    /// point but the network reaction below is one.</param>
    public static void Activate(KeepAwakeRequest request, string cause = "user request")
    {
        var now = DateTimeOffset.Now;
        var session = new KeepAwakeSession(request, now, KeepAwakePolicy.ExpiryFor(request, now));
        lock (_sync)
        {
            EnsureHolder();
            _current = session;
            PostLocked(HoldFlags());
            ArmExpiry(session, now);
        }
        PowerLog.Event($"Keep-awake on, {KeepAwakePolicy.DescribeRemaining(now, session)}", cause);
        RaiseStateChanged();
    }

    /// <summary>Ends the session and releases the OS hold. No-op when nothing is running.</summary>
    /// <param name="cause">Who asked — see <see cref="Activate"/>.</param>
    public static void Deactivate(string cause = "user request")
    {
        lock (_sync)
        {
            if (_current is null) return;
            ClearLocked();
        }
        PowerLog.Event("Keep-awake off", cause);
        RaiseStateChanged();
    }

    /// <summary>
    /// A timer's due time elapses in suspended wall-clock time without firing, so a machine that
    /// slept past its expiry has to be expired on wake; anything left is re-armed.
    /// </summary>
    public static void OnPowerResume()
    {
        bool expired = false;
        lock (_sync)
        {
            if (_current is not { } session) return;
            var now = DateTimeOffset.Now;
            if (KeepAwakePolicy.ShouldExpire(now, session.ExpiresAt)) { ClearLocked(); expired = true; }
            else ArmExpiry(session, now);
        }
        if (expired)
        {
            PowerLog.Event("Keep-awake off", "the session expired while the machine was asleep");
            RaiseStateChanged();
        }
    }

    /// <summary>
    /// Leaving the network ends an <see cref="KeepAwakeKind.UntilNetworkChange"/> session; arriving
    /// somewhere whose first matching rule sets <see cref="NetworkLocationRule.KeepAwakeHere"/>
    /// starts one. Gated on <see cref="AppSettings.NetworkProfilesEnabled"/>, and never overrides a
    /// session the user started by hand.
    /// </summary>
    private static void OnLocationChanged(NetworkLocation location)
    {
        if (Current?.Request.Kind == KeepAwakeKind.UntilNetworkChange)
            Deactivate("left the network the session was tied to");

        var s = SettingsService.Current;
        if (Current is null && s.NetworkProfilesEnabled && s.FindNetworkRule(location) is { KeepAwakeHere: true })
            Activate(new KeepAwakeRequest(KeepAwakeKind.UntilNetworkChange, null, null),
                     $"network rule for '{location.DisplayHint ?? location.IpCidr ?? "this network"}'");
    }

    private static uint HoldFlags() =>
        NativeMethods.ES_CONTINUOUS | NativeMethods.ES_SYSTEM_REQUIRED |
        (SettingsService.Current.KeepAwakeDisplayOn ? NativeMethods.ES_DISPLAY_REQUIRED : 0);

    // Callers hold _sync. One place posts, so _postedFlags cannot drift from the queue.
    private static void PostLocked(uint flags)
    {
        _postedFlags = flags;
        _holdRequests.Add(flags);
    }

    private static void EnsureHolder()
    {
        if (_holder is not null) return;
        // Background thread: process exit tears it down, which releases the execution state anyway.
        _holder = new Thread(HolderLoop) { IsBackground = true, Name = "KeepAwake" };
        _holder.Start();
    }

    private static void HolderLoop()
    {
        foreach (uint flags in _holdRequests.GetConsumingEnumerable())
        {
            try
            {
                NativeMethods.SetThreadExecutionState(flags);
                // Logged here, not at the request sites: this is when the OS learns about the hold.
                PowerLog.Event(
                    flags == NativeMethods.ES_CONTINUOUS
                        ? "OS keep-awake hold released"
                        : $"OS keep-awake hold taken, display {((flags & NativeMethods.ES_DISPLAY_REQUIRED) != 0 ? "held on" : "free to sleep")}",
                    "keep-awake session");
            }
            catch (Exception ex) { AppLog.Error("KeepAwakeService.SetThreadExecutionState", ex); }
        }
    }

    // Callers hold _sync.
    private static void ArmExpiry(KeepAwakeSession session, DateTimeOffset now)
    {
        _expiryTimer?.Dispose();
        _expiryTimer = null;
        if (session.ExpiresAt is not { } expiry) return;   // no clock expiry to arm

        var due = expiry - now;
        if (due < TimeSpan.Zero) due = TimeSpan.Zero;
        // One timer armed to the instant, not a poll — an until-time is at most 24 h out, well inside
        // Timer's range.
        _expiryTimer = new System.Threading.Timer(_ => ExpireIfDue(), null, due, Timeout.InfiniteTimeSpan);
    }

    private static void ExpireIfDue()
    {
        lock (_sync)
        {
            // Re-check rather than trusting the callback: the session may have been replaced or ended
            // between the timer firing and this taking the lock.
            if (_current is not { } session || !KeepAwakePolicy.ShouldExpire(DateTimeOffset.Now, session.ExpiresAt))
                return;
            ClearLocked();
        }
        PowerLog.Event("Keep-awake off", "the session reached its own expiry time");
        RaiseStateChanged();
    }

    // Never let a subscriber's failure escape: two of these raise sites are timer callbacks, where an
    // escaped exception terminates the process, and the window subscribers touch the UI.
    private static void RaiseStateChanged()
    {
        try { StateChanged?.Invoke(); }
        catch (Exception ex) { AppLog.Error("KeepAwakeService.StateChanged", ex); }
    }

    // Callers hold _sync.
    private static void ClearLocked()
    {
        _current = null;
        _expiryTimer?.Dispose();
        _expiryTimer = null;
        // Clearing must happen on the thread that made the request — post it, don't call it here.
        if (_holder is not null) PostLocked(NativeMethods.ES_CONTINUOUS);
    }
}
