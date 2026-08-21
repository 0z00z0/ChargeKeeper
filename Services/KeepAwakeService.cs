using System.Collections.Concurrent;
using ChargeKeeper.Helpers;

namespace ChargeKeeper.Services;

/// <summary>
/// Holds the machine awake for a bounded session (issue #90). The clock rules live in the pure
/// <see cref="KeepAwakePolicy"/>; this owns the OS hold, the expiry timer and the network reactions.
/// <para>
/// <c>SetThreadExecutionState</c> is PER-THREAD: the request dies with the thread that made it, so a
/// call from a thread-pool thread would be dropped the moment that thread is recycled. The hold
/// therefore lives on one dedicated long-lived thread fed by a request queue, and clearing means
/// posting <c>ES_CONTINUOUS</c> alone to that SAME thread. The thread is a background thread, so
/// process exit releases the hold for free.
/// </para>
/// <para>
/// The active session is deliberately NOT persisted: keep-awake surviving a reboot would be a
/// surprise, and reconstructing expiry across a dead process buys nothing. Only the presets and the
/// display-on preference are settings.
/// </para>
/// </summary>
internal static class KeepAwakeService
{
    // Guards _current + _expiryTimer + the holder-thread start. StateChanged is raised OUTSIDE the
    // lock so a slow subscriber (a tray/tooltip rebuild) can't stall an expiry or a location change.
    private static readonly System.Threading.Lock _sync = new();
    private static KeepAwakeSession? _current;
    private static System.Threading.Timer? _expiryTimer;

    // The dedicated holder thread and its request queue: each item is the exact esFlags value to
    // apply. One thread for the app's lifetime — see the class note on the per-thread state.
    private static readonly BlockingCollection<uint> _holdRequests = new();
    private static Thread? _holder;

    private static bool _started;

    /// <summary>Raised (off the UI thread) whenever a session starts, ends, or expires.</summary>
    public static event Action? StateChanged;

    /// <summary>The running session, or null when nothing is holding the machine awake.</summary>
    public static KeepAwakeSession? Current { get { lock (_sync) return _current; } }

    /// <summary>
    /// Wires the network-location reactions. Called once at startup next to
    /// <see cref="NetworkLocationService.Start"/>; never unsubscribed, same "lives for the whole
    /// process" reasoning as TrayMenu's own subscription.
    /// </summary>
    public static void Start()
    {
        lock (_sync)
        {
            if (_started) return;
            _started = true;
        }
        NetworkLocationService.LocationChanged += OnLocationChanged;
    }

    /// <summary>
    /// Starts (or replaces) the keep-awake session. Re-applies the OS hold on every call so a change
    /// to <see cref="AppSettings.KeepAwakeDisplayOn"/> takes effect on the next activation.
    /// </summary>
    /// <param name="cause">Who asked, for the power trail. Defaults to the user because every other
    /// entry point (tray toggle, dashboard, Settings) is one; the network reaction below says so.</param>
    public static void Activate(KeepAwakeRequest request, string cause = "user request")
    {
        var now = DateTimeOffset.Now;
        var session = new KeepAwakeSession(request, now, KeepAwakePolicy.ExpiryFor(request, now));
        lock (_sync)
        {
            EnsureHolder();
            _current = session;
            _holdRequests.Add(HoldFlags());
            ArmExpiry(session, now);
        }
        PowerLog.Event($"Keep-awake on, {KeepAwakePolicy.DescribeRemaining(now, session)}", cause);
        StateChanged?.Invoke();
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
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Re-evaluates the session after a resume from standby. A machine that slept past its expiry must
    /// end the session on wake — the timer's due time elapses in suspended wall-clock time and does not
    /// fire — so expire when due and otherwise re-arm to what is genuinely left.
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
            StateChanged?.Invoke();
        }
    }

    /// <summary>
    /// Network-location reaction (issue #90). Two rules: leaving the network ends an
    /// <see cref="KeepAwakeKind.UntilNetworkChange"/> session, and arriving somewhere whose first
    /// matching rule sets <see cref="NetworkLocationRule.KeepAwakeHere"/> starts one — so leaving is
    /// then the natural off switch, mirroring how charge presets follow the network. Auto-activate is
    /// gated on <see cref="AppSettings.NetworkProfilesEnabled"/> (the rules are inert otherwise) and
    /// never overrides a session the user started by hand.
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

    // ── OS hold ───────────────────────────────────────────────────────────────────

    private static uint HoldFlags() =>
        NativeMethods.ES_CONTINUOUS | NativeMethods.ES_SYSTEM_REQUIRED |
        (SettingsService.Current.KeepAwakeDisplayOn ? NativeMethods.ES_DISPLAY_REQUIRED : 0);

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
                // Logged HERE rather than at the request sites: this is the moment the OS actually
                // learns about the hold, and ES_CONTINUOUS on its own is the release. The display
                // flag is named because "stayed awake but the screen slept" is its own bug report.
                PowerLog.Event(
                    flags == NativeMethods.ES_CONTINUOUS
                        ? "OS keep-awake hold released"
                        : $"OS keep-awake hold taken, display {((flags & NativeMethods.ES_DISPLAY_REQUIRED) != 0 ? "held on" : "free to sleep")}",
                    "keep-awake session");
            }
            catch (Exception ex) { AppLog.Error("KeepAwakeService.SetThreadExecutionState", ex); }
        }
    }

    // ── Expiry ────────────────────────────────────────────────────────────────────

    // Callers hold _sync.
    private static void ArmExpiry(KeepAwakeSession session, DateTimeOffset now)
    {
        _expiryTimer?.Dispose();
        _expiryTimer = null;
        if (session.ExpiresAt is not { } expiry) return;   // no clock expiry to arm

        var due = expiry - now;
        if (due < TimeSpan.Zero) due = TimeSpan.Zero;
        // One timer armed to the exact instant, not a poll — an until-time is at most 24 h out, well
        // inside Timer's range, so no clamping is needed.
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
        StateChanged?.Invoke();
    }

    // Callers hold _sync.
    private static void ClearLocked()
    {
        _current = null;
        _expiryTimer?.Dispose();
        _expiryTimer = null;
        // Clearing must happen on the thread that made the request — post it, don't call it here.
        if (_holder is not null) _holdRequests.Add(NativeMethods.ES_CONTINUOUS);
    }
}
