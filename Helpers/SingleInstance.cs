namespace ChargeKeeper.Helpers;

/// <summary>
/// The process-wide "only one ChargeKeeper" lock.
///
/// Diagnosed 2026-07-03: after install, the elevated post-install launch can go unnoticed (the UAC
/// prompt isn't always where the user is looking), so they launch the app again themselves —
/// nothing stopped a second process from running alongside the first. Two instances would both
/// claim the tray icon and write to battery-level-history.csv with no cross-process locking.
///
/// <para>Once acquired the lock is held for the WHOLE process lifetime and never released. Windows
/// drops it automatically on termination (clean exit, crash, or kill), which also sidesteps Mutex's
/// same-thread-release requirement — a ProcessExit handler isn't guaranteed to run on the thread
/// that acquired it.</para>
///
/// <para>Lives here rather than in App because <see cref="Program"/> needs it too: a watchdog probe
/// decides whether to resurrect the app by trying to claim this lock, and does so BEFORE any XAML
/// exists. <see cref="IsHeld"/> is what keeps those two acquire sites from fighting each other.</para>
/// </summary>
internal static class SingleInstance
{
    private const string MutexName = "Local\\ChargeKeeper.SingleInstance";

    // Only ever touched from the main thread: Program.Main, then App.OnLaunched, which the
    // dispatcher runs on that same thread. Never released — see the type comment — so the handle is
    // kept alive here purely so the GC can't finalize it out from under a running app.
    private static Mutex? _mutex;

    /// <summary>Whether THIS process already owns the lock, i.e. an acquire has succeeded.</summary>
    internal static bool IsHeld => _mutex is not null;

    /// <summary>
    /// One instant, non-blocking attempt to claim the lock. True means this process now owns it.
    /// </summary>
    internal static bool TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: false, MutexName);

        bool acquired;
        try
        {
            acquired = mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            // The previous owner died still holding it — a hard kill, or the GPU-teardown the
            // self-heal exists for — while THIS process already had a handle open (the retry loop
            // below makes that window real, and it is the exact case the retry is FOR). The wait
            // still SUCCEEDED: the kernel hands the mutex over and only flags that whatever the dead
            // owner was protecting may be half-written. This mutex protects nothing but "one
            // instance", which needs no repair, so take it. Left unhandled it would have thrown out
            // of the async void OnLaunched and killed the resurrection it was in the middle of.
            acquired = true;
        }

        if (acquired)
        {
            _mutex = mutex;
            return true;
        }

        mutex.Dispose();
        return false;
    }

    /// <summary>
    /// Retries <see cref="TryAcquire"/> up to <paramref name="attempts"/> times, ~200 ms apart,
    /// before giving up. See <see cref="StartupArgs.SingleInstanceAttempts"/> for which launches
    /// deserve how many — the retry is not free (it is silent, invisible dead time) and exists for
    /// one specific race, not as a general courtesy.
    /// </summary>
    internal static async Task<bool> TryAcquireAsync(int attempts)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (TryAcquire()) return true;
            // No trailing delay after the last attempt — nothing would observe it, and it made
            // attempts:1 cost 200 ms for no reason.
            if (attempt < attempts - 1)
                await Task.Delay(200).ConfigureAwait(true);
        }
        return false;
    }
}
