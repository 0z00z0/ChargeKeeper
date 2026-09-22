using Windows.System.Power;

namespace ChargeKeeper.Services;

/// <summary>Manages the "charge to 100 % once" travel override: parks the Smart Charge thresholds,
/// disables the cap so the battery reaches 100 %, then puts the parked pair back as soon as the
/// charge it was asked for is over — at full, or when the charger is removed, whichever comes first.
/// The whole record is persisted, so a restart or a crash resumes the same single charge rather than
/// leaving the cap off for every later one.</summary>
internal static class TravelOverrideService
{
    public static bool IsActive => SettingsService.Current.TravelOverrideActive;

    /// <summary>False when Smart Charge was already off, so <see cref="Cancel"/> writes nothing.</summary>
    public static bool HasSavedRevertThresholds =>
        SettingsService.Current is { TravelOverrideRevertStart: not null, TravelOverrideRevertStop: not null };

    /// <summary>Whether the machine has charged under this lift. On disk rather than in memory: a
    /// lift armed on battery must still be waiting for its charger after a restart, and one whose
    /// charge had begun must still end when the charger turns out to be gone.</summary>
    public static bool ChargeStarted => SettingsService.Current.TravelOverrideChargeStarted;

    /// <summary>The thresholds to show and publish while the lift is in force — the parked pair,
    /// which is the setting the user actually chose and the one that comes back. Null when Smart
    /// Charge was already off at activation, so nothing is owed back and nothing is claimed.</summary>
    public static (int Start, int Stop)? ParkedThresholds =>
        SettingsService.Current is { TravelOverrideActive: true,
                                     TravelOverrideRevertStart: { } start,
                                     TravelOverrideRevertStop:  { } stop }
            ? (start, stop)
            : null;

    /// <summary>Raised on a background thread once an activation or revert has settled. The tray tooltip
    /// is not driven by a battery event, so it refreshes from here.</summary>
    public static event Action? StateChanged;

    public static string ActionLabel =>
        IsActive ? "✕  Revert to charge threshold" : "🔝  Charge to 100 % once";

    // Fire-once latch (0 = armed, 1 = revert dispatched). ApplyRevert clears TravelOverrideActive
    // asynchronously, so IsActive lags a few battery ticks behind the dispatch and would otherwise
    // let a second one through. Interlocked: OnBatteryReport runs on the MTA battery thread.
    private static int _revertDispatched;

    // Previous status, for the Charging→Idle edge. A race here at worst misses one edge, and the
    // pct≥100 fallback still reverts.
    private static BatteryStatus _lastStatus = BatteryStatus.NotPresent;

    /// <summary>Saves the current thresholds, then disables Smart Charge so the battery reaches 100 %.</summary>
    public static void Activate()
    {
        Task.Run(() =>
        {
            var state = ChargeThresholdService.Read();

            // Update() so a Reload() during this Task's async gap cannot orphan the mutation.
            SettingsService.Update(s =>
            {
                // IsLimiting, not Start > 0: HP and Surface report Start as 0 by contract, and
                // testing it would leave nothing to restore on those machines.
                if (state is { IsLimiting: true })
                {
                    s.TravelOverrideRevertStart = state.Start;
                    s.TravelOverrideRevertStop  = state.Stop;
                }
                else
                {
                    s.TravelOverrideRevertStart = null;
                    s.TravelOverrideRevertStop  = null;
                }

                s.TravelOverrideActive        = true;
                s.TravelOverrideChargeStarted = false;   // armed; the charger may not be in yet
            });

            // A rejected write leaves the override armed over an unchanged device — log it, or it
            // looks identical to a success.
            if (!ChargeThresholdService.SetEnabled(false))
                AppLog.Info("TravelOverride: activation rejected by the device — thresholds unchanged.");

            StateChanged?.Invoke();   // refresh the tooltip now, don't wait for a battery event
        });
    }

    public static void Cancel() => ApplyRevert();

    /// <summary>Clears the override without touching the thresholds, for when an explicit new choice
    /// supersedes it. Restoring the saved pair (what <see cref="Cancel"/> does) would clobber the
    /// caller's own write; leaving it armed would let the auto-revert clobber it later.</summary>
    public static void Deactivate()
    {
        if (!IsActive) return;
        ClearOverrideState();
    }

    /// <summary>The single primitive every "apply a Start/Stop" caller funnels through.
    /// <see cref="Deactivate"/> must run FIRST: an armed auto-revert would otherwise clobber the new
    /// thresholds at the next full charge.</summary>
    public static bool ApplyExplicitThresholds(int start, int stop)
    {
        // Snapshot before Deactivate clears it. The saved pair is the only record of the user's real
        // thresholds, so a rejected write has to put it back.
        var s = SettingsService.Current;
        var (wasActive, revertStart, revertStop, chargeStarted) =
            (s.TravelOverrideActive, s.TravelOverrideRevertStart, s.TravelOverrideRevertStop,
             s.TravelOverrideChargeStarted);

        Deactivate();

        // Valid non-zero thresholds enable Smart Charge by themselves, so no SetEnabled first.
        if (ChargeThresholdService.SetThresholds(start, stop)) return true;

        if (wasActive)
            SettingsService.Update(x =>
            {
                x.TravelOverrideActive        = true;
                x.TravelOverrideRevertStart   = revertStart;
                x.TravelOverrideRevertStop    = revertStop;
                x.TravelOverrideChargeStarted = chargeStarted;
            });
        return false;
    }

    /// <summary>Shared by <see cref="Deactivate"/> (clear only) and <see cref="ApplyRevert"/> (restore, then clear).</summary>
    private static void ClearOverrideState()
    {
        SettingsService.Update(s =>
        {
            s.TravelOverrideActive        = false;
            s.TravelOverrideRevertStart   = null;
            s.TravelOverrideRevertStop    = null;
            s.TravelOverrideChargeStarted = false;
        });

        StateChanged?.Invoke();   // tray tooltip + menu resync immediately
    }

    /// <summary>Feeds one battery reading to <see cref="TravelOverridePolicy"/> and carries out what
    /// it decides. Every ending — the charge completing and the charger being removed — is read
    /// there, not here.</summary>
    public static void OnBatteryReport(int pct, BatteryStatus status)
    {
        if (!IsActive)
        {
            _lastStatus = status;
            Interlocked.Exchange(ref _revertDispatched, 0);   // re-arm for the next activation
            return;
        }

        var last = _lastStatus;
        _lastStatus = status;

        switch (TravelOverridePolicy.Decide(active: true, ChargeStarted, pct, status, last))
        {
            case TravelOverrideStep.RecordChargeStarted:
                // Straight to disk: a crash between here and the charger coming out would otherwise
                // leave the lift waiting for a charge that already happened.
                SettingsService.Update(s => s.TravelOverrideChargeStarted = true);
                break;

            // CAS the latch so only the first qualifying report dispatches the revert.
            case TravelOverrideStep.Revert when Interlocked.CompareExchange(ref _revertDispatched, 1, 0) == 0:
                ApplyRevert();
                break;
        }
    }

    private static void ApplyRevert()
    {
        // Read synchronously, before the async gap below. Only the write side needs Update().
        var s           = SettingsService.Current;
        var revertStart = s.TravelOverrideRevertStart;
        var revertStop  = s.TravelOverrideRevertStop;
        Task.Run(() =>
        {
            try
            {
                if (revertStart is { } start && revertStop is { } stop)
                {
                    // Attempt both writes, then judge — enabling is not a precondition for the
                    // threshold write.
                    bool ok = ChargeThresholdService.SetEnabled(true);
                    ok     &= ChargeThresholdService.SetThresholds(start, stop);
                    if (!ok)
                    {
                        // Keep flag and values: they are the only record of the user's real
                        // thresholds, and the device is still at 0/0. Re-arm here too — the flag
                        // stays true, so OnBatteryReport's re-arm never runs and one rejection would
                        // otherwise ignore every later completion edge.
                        AppLog.Info($"TravelOverride: revert to {start}/{stop} rejected by the device — " +
                                    "override left active, saved thresholds kept.");
                        Interlocked.Exchange(ref _revertDispatched, 0);
                        return;
                    }
                }
                // Nothing was saved, so Smart Charge stays disabled.

                ClearOverrideState();
            }
            catch (Exception ex)
            {
                // Same reasoning as the rejection path: the override is still active, so
                // OnBatteryReport's re-arm never runs and a throw would disarm auto-revert for good.
                AppLog.Error("TravelOverrideService.ApplyRevert", ex);
                Interlocked.Exchange(ref _revertDispatched, 0);
            }
        });
    }
}
