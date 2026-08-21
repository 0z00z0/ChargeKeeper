namespace ChargeKeeper.Helpers;

/// <summary>
/// The process-wide "only one ChargeKeeper" lock — two instances would both claim the tray icon and
/// write the history CSV with no cross-process locking.
/// <para>Once acquired the lock is held for the whole process lifetime and never released. Windows
/// drops it on termination, which also sidesteps Mutex's same-thread-release requirement: a
/// ProcessExit handler is not guaranteed to run on the thread that acquired it.</para>
/// </summary>
internal static class SingleInstance
{
    private const string MutexName = "Local\\ChargeKeeper.SingleInstance";

    // Only ever touched from the main thread. Never released, so the handle is kept alive here purely
    // so the GC cannot finalize it out from under a running app.
    private static Mutex? _mutex;

    /// <summary>Whether THIS process already owns the lock, i.e. an acquire has succeeded.</summary>
    internal static bool IsHeld => _mutex is not null;

    /// <summary>One instant, non-blocking attempt to claim the lock.</summary>
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
            // The previous owner died holding it. The wait still SUCCEEDED — the kernel hands the
            // mutex over and only flags that what the dead owner protected may be half-written. This
            // one protects nothing but "one instance", which needs no repair, so take it.
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

    /// <summary>Retries <see cref="TryAcquire"/> up to <paramref name="attempts"/> times, ~200 ms
    /// apart. The wait is silent, invisible dead time, so see
    /// <see cref="StartupArgs.SingleInstanceAttempts"/> for which launches deserve how many.</summary>
    internal static async Task<bool> TryAcquireAsync(int attempts)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (TryAcquire()) return true;
            // No trailing delay after the last attempt — nothing would observe it.
            if (attempt < attempts - 1)
                await Task.Delay(200).ConfigureAwait(true);
        }
        return false;
    }
}
