using ChargeKeeper.Helpers;

namespace ChargeKeeper.Services;

/// <summary>
/// Turns physical mouse and keyboard input off for the length of a focus session, and is built so
/// that every way of losing control of it ends with input back.
/// </summary>
/// <remarks>
/// <para>Windows only lets the thread that blocked input release it, so the block lives on a thread
/// of its own. That thread holds a deadline rather than a flag: it releases and ends unless
/// something keeps pushing the deadline forward, so the application hanging lifts the block within
/// the renewal window instead of leaving a machine nobody can type on.</para>
/// <para>Each renewal also carries the session's own end time, so the block lifts at that instant
/// whether or not anything is still renewing it.</para>
/// <para>Ctrl+Alt+Delete lifts the block, and the loop puts it back on the next pass. The cost is
/// that the secure desktop is the only place a person can act: signing out or restarting from that
/// screen is the way out of a session whose machine will not answer.</para>
/// </remarks>
internal static class InputBlock
{
    /// <summary>How long the block outlives its last renewal. Three of the focus session's own
    /// one-second ticks, so a tick that is merely late does not drop the block.</summary>
    internal static readonly TimeSpan RenewalWindow = TimeSpan.FromSeconds(3);

    /// <summary>How often the thread re-asserts the block and re-reads its deadline. Short enough
    /// that Ctrl+Alt+Delete's lift is put back promptly, long enough not to spin.</summary>
    private static readonly TimeSpan Pass = TimeSpan.FromMilliseconds(250);

    private static readonly Lock _gate = new();

    private static Thread? _thread;
    private static DateTimeOffset _renewedAt;
    private static DateTimeOffset _until;
    private static bool _stopping;

    /// <summary>Whether Windows accepted the block. Written by the holding thread before it signals
    /// that it is ready and read by the caller afterwards, so the signal is what orders the two —
    /// taking the lock before signalling would deadlock against the caller holding it.</summary>
    private static volatile bool _took;

    /// <summary>Whether a block is being held right now.</summary>
    internal static bool IsBlocking { get { lock (_gate) return _thread is not null && !_stopping; } }

    /// <summary>Why input cannot be blocked now, or null when it can. Without administrator rights
    /// Windows refuses every call, so a session would arm a lever that does nothing.</summary>
    internal static string? Refusal() =>
        Elevation.IsElevated
            ? null
            : "blocking the mouse and keyboard needs administrator rights, which this run does not have";

    /// <summary>Blocks input until the renewals stop, or until the session's end time once the
    /// first renewal has supplied it. False means Windows refused the block, which is a lever that
    /// did not engage.</summary>
    internal static bool Take(ActionCause cause)
    {
        lock (_gate)
        {
            // No end time is known here: the session's own tick supplies one within a second, and
            // until it does the renewal deadline alone is what bounds the block.
            _until = DateTimeOffset.MaxValue;
            _renewedAt = DateTimeOffset.UtcNow;

            if (_thread is not null) return true;

            _stopping = false;
            _took = false;

            var ready = new ManualResetEventSlim(false);
            _thread = new Thread(() => Hold(ready))
            {
                IsBackground = true,
                Name = "ChargeKeeper input block",
            };
            _thread.Start();
            ready.Wait(TimeSpan.FromSeconds(2));

            AppLog.Info(_took
                ? $"Focus: the mouse and keyboard are blocked ({cause})."
                : $"Focus: Windows refused to block the mouse and keyboard ({cause}).");
            return _took;
        }
    }

    /// <summary>Pushes the deadline forward. Called from the session's own tick: stop calling it and
    /// the block lifts by itself.</summary>
    internal static void Renew(DateTimeOffset until)
    {
        lock (_gate)
        {
            if (_thread is null) return;
            _until = until;
            _renewedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>Releases the block. True once nothing is held, including when nothing was.</summary>
    internal static bool Release(ActionCause cause)
    {
        Thread? thread;
        lock (_gate)
        {
            if (_thread is null) return true;
            _stopping = true;
            thread = _thread;
        }

        thread.Join(TimeSpan.FromSeconds(3));
        lock (_gate)
        {
            bool ended = !thread.IsAlive;
            if (ended) _thread = null;
            AppLog.Info(ended
                ? $"Focus: the mouse and keyboard are back ({cause})."
                : $"Focus: the input block did not release, and lapses by itself within "
                + $"{RenewalWindow.TotalSeconds:0} seconds ({cause}).");
            return ended;
        }
    }

    /// <summary>The whole of the block's life, on one thread because Windows accepts the release
    /// only from the thread that took it.</summary>
    private static void Hold(ManualResetEventSlim ready)
    {
        bool holding = false;
        try
        {
            holding = NativeMethods.SetInputBlocked(true);
            _took = holding;
            ready.Set();
            if (!holding) return;

            while (true)
            {
                Thread.Sleep(Pass);

                bool lapse;
                lock (_gate)
                {
                    lapse = _stopping
                         || DateTimeOffset.UtcNow - _renewedAt > RenewalWindow
                         || DateTimeOffset.UtcNow >= _until;
                }
                if (lapse) return;

                // Ctrl+Alt+Delete lifts the block; this is what puts it back. The call is accepted
                // again from the thread that holds it, so re-asserting costs nothing when it is
                // still in force.
                NativeMethods.SetInputBlocked(true);
            }
        }
        catch (Exception ex) { AppLog.Error("InputBlock.Hold", ex); }
        finally
        {
            ready.Set();
            if (holding) NativeMethods.SetInputBlocked(false);
            lock (_gate) { if (_thread == Thread.CurrentThread) _thread = null; }
        }
    }
}

/// <summary>Whether this run holds administrator rights. The manifest asks for them, so it is a
/// reading rather than a question — but the input block is refused outright without them, and a
/// lever that quietly does nothing is worse than one that says why.</summary>
internal static class Elevation
{
    private static readonly Lazy<bool> _elevated = new(() =>
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) { AppLog.Error("Elevation.IsElevated", ex); return false; }
    });

    internal static bool IsElevated => _elevated.Value;
}
