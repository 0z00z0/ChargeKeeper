using ChargeKeeper.Helpers;

namespace ChargeKeeper.Services;

/// <summary>
/// Runs lid-bound scripts from a lid-switch subscription of its own, independent of
/// <see cref="LidDelayService"/> — a script bound to the lid must fire whether or not Lid delay is
/// switched on. Windows accepts more than one lid-switch registration per process, so this holds its
/// own handle and its own replay state rather than sharing Lid delay's.
/// </summary>
internal static class ScriptLidTrigger
{
    private static readonly System.Threading.Lock _sync = new();
    private static IntPtr _registration = IntPtr.Zero;
    private static bool   _seeded;
    private static bool?  _lastPayload;

    /// <summary>Starts the subscription. Safe to call more than once — a live registration is left as
    /// it is.</summary>
    public static void Start()
    {
        lock (_sync)
        {
            if (_registration != IntPtr.Zero) return;
            _seeded = false;   // the next callback is this registration's own replay
            var registration = NativeMethods.RegisterLidNotification(OnLidState);
            if (registration == IntPtr.Zero)
            {
                AppLog.Error("ScriptLidTrigger.Start: could not subscribe to the lid switch", null);
                return;
            }
            _registration = registration;
        }
    }

    /// <summary>Ends the subscription. Safe to call when none is live.</summary>
    public static void Stop()
    {
        IntPtr registration;
        lock (_sync)
        {
            registration  = _registration;
            _registration = IntPtr.Zero;
        }
        NativeMethods.UnregisterLidNotification(registration);
    }

    /// <summary>Lid-switch callback — arrives on an OS thread, so it must not block.</summary>
    private static void OnLidState(byte payload)
    {
        bool closed = payload == LidEventLog.ClosedPayload;
        LidEventKind kind;
        lock (_sync)
        {
            bool first = !_seeded;
            kind = LidEventLog.KindOf(closed, first ? null : _lastPayload);
            _lastPayload = closed;
            _seeded      = true;
        }

        if (ScriptTriggerPolicy.ForLid(kind) is { } trigger) ScriptRunner.Instance.Fire(trigger);
    }
}
