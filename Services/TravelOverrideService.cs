using Windows.System.Power;

namespace ChargeKeeper.Services;

/// <summary>Manages the "charge to 100 % once" travel override: saves the Smart Charge threshold,
/// disables it so the battery reaches 100 %, then restores it once <see cref="OnBatteryReport"/> sees
/// charging complete. Persisted, so the override survives a restart mid-charge.</summary>
internal static class TravelOverrideService
{
    public static bool IsActive => SettingsService.Current.TravelOverrideActive;

    /// <summary>False when Smart Charge was already off, so <see cref="Cancel"/> writes nothing.</summary>
    public static bool HasSavedRevertThresholds =>
        SettingsService.Current is { TravelOverrideRevertStart: not null, TravelOverrideRevertStop: not null };

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

                s.TravelOverrideActive = true;
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
        var (wasActive, revertStart, revertStop) =
            (s.TravelOverrideActive, s.TravelOverrideRevertStart, s.TravelOverrideRevertStop);

        Deactivate();

        // Valid non-zero thresholds enable Smart Charge by themselves, so no SetEnabled first.
        if (ChargeThresholdService.SetThresholds(start, stop)) return true;

        if (wasActive)
            SettingsService.Update(x =>
            {
                x.TravelOverrideActive      = true;
                x.TravelOverrideRevertStart = revertStart;
                x.TravelOverrideRevertStop  = revertStop;
            });
        return false;
    }

    /// <summary>Shared by <see cref="Deactivate"/> (clear only) and <see cref="ApplyRevert"/> (restore, then clear).</summary>
    private static void ClearOverrideState()
    {
        SettingsService.Update(s =>
        {
            s.TravelOverrideActive      = false;
            s.TravelOverrideRevertStart = null;
            s.TravelOverrideRevertStop  = null;
        });

        StateChanged?.Invoke();   // tray tooltip + menu resync immediately
    }

    /// <summary>Reverts once the override is active and charging has completed. "Complete" is the
    /// Charging→Idle edge (catches a worn battery settling below 100 %) or Idle at 100 % (catches
    /// firmware that never reports a Charging phase) — each alone misses one case.</summary>
    public static void OnBatteryReport(int pct, BatteryStatus status)
    {
        if (!IsActive)
        {
            _lastStatus = status;
            Interlocked.Exchange(ref _revertDispatched, 0);   // re-arm for the next activation
            return;
        }

        bool chargingJustCompleted = _lastStatus == BatteryStatus.Charging &&
                                     status      == BatteryStatus.Idle;
        bool fullAndIdle           = status == BatteryStatus.Idle && pct >= 100;
        _lastStatus = status;

        // CAS the latch so only the first qualifying report dispatches the revert.
        if ((chargingJustCompleted || fullAndIdle) &&
            Interlocked.CompareExchange(ref _revertDispatched, 1, 0) == 0)
        {
            ApplyRevert();
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
