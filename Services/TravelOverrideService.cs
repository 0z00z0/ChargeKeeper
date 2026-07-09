using Windows.System.Power;

namespace ChargeKeeper.Services;

/// <summary>
/// Manages the "charge to 100 % once" travel override.
/// <para>
/// When <see cref="Activate"/> is called the current Smart Charge threshold is saved
/// to settings and the threshold is disabled so the battery can charge to 100 %.
/// <c>App</c> feeds every battery reading to <see cref="OnBatteryReport"/>; once the battery
/// reaches a full/idle state the saved threshold is restored automatically. The user can also
/// cancel early via <see cref="Cancel"/>.
/// </para>
/// <para>
/// State is persisted so the override survives an app restart mid-charge.
/// </para>
/// </summary>
internal static class TravelOverrideService
{
    /// <summary>True while a travel override is active.</summary>
    public static bool IsActive => SettingsService.Current.TravelOverrideActive;

    /// <summary>
    /// Raised once the override has been activated or reverted and the state has settled
    /// (threshold written + TravelOverrideActive saved). The tray tooltip isn't driven by a
    /// battery event, so it subscribes to this to refresh immediately instead of staying stale.
    /// Fires on a background thread.
    /// </summary>
    public static event Action? StateChanged;

    /// <summary>The dashboard's action-button label for the override, reflecting its current
    /// state. (The tray menu no longer uses this — it shows a constant-caption toggle item with
    /// a check mark instead, see <c>TrayMenu</c>.) 🔝 ("to 100 %") matches the tooltip's
    /// "Charging to 100 %" line.</summary>
    public static string ActionLabel =>
        IsActive ? "✕  Revert to charge threshold" : "🔝  Charge to 100 % once";

    // Fire-once latch for the current activation (0 = armed, 1 = revert dispatched). ApplyRevert
    // clears TravelOverrideActive only asynchronously (background Task), so IsActive lags for a
    // few battery ticks after the revert is dispatched — this latch stops a second dispatch.
    // Reset to 0 whenever the override is no longer active, readying it for the next activation.
    // Swapped with Interlocked because OnBatteryReport runs on the MTA battery thread and the
    // synchronous startup call can overlap an OS report — the CAS guarantees a single winner.
    private static int _revertDispatched;

    // Previous battery status, for detecting the Charging→Idle completion edge. Only touched
    // from OnBatteryReport; a benign race there at worst misses one edge (the next tick, or the
    // pct≥100 fallback, still reverts).
    private static BatteryStatus _lastStatus = BatteryStatus.NotPresent;

    /// <summary>
    /// Saves the current thresholds and disables Smart Charge so the battery charges to 100 %.
    /// </summary>
    public static void Activate()
    {
        Task.Run(() =>
        {
            var state = ChargeThresholdService.Read();

            // Update() reads/mutates/saves atomically — a plain "var s = Current; ...; Save();"
            // here would race SettingsService.Reload() (the "Reload settings from disk" menu
            // command): Reload can swap Current out from under this Task.Run's async gap (the RPC
            // read above), orphaning the mutation and silently losing it (see Update's doc comment).
            SettingsService.Update(s =>
            {
                // Remember what to restore only when Smart Charge is on with valid values.
                if (state is { Capable: true, Enabled: true, Start: > 0, Stop: > 0 })
                {
                    s.TravelOverrideRevertStart = state.Start;
                    s.TravelOverrideRevertStop  = state.Stop;
                }
                else
                {
                    // Was already disabled — nothing to restore.
                    s.TravelOverrideRevertStart = null;
                    s.TravelOverrideRevertStop  = null;
                }

                s.TravelOverrideActive = true;
            });

            // Disable threshold (start=0, stop=0 → charge to 100 %).
            ChargeThresholdService.SetEnabled(false);

            StateChanged?.Invoke();   // refresh the tooltip now, don't wait for a battery event
        });
    }

    /// <summary>Immediately cancels the override and restores the previous thresholds.</summary>
    public static void Cancel() => ApplyRevert();

    /// <summary>
    /// Clears the override state WITHOUT touching the charge thresholds — for when an explicit
    /// new threshold choice (e.g. applying a preset) supersedes the override. The caller is about
    /// to write thresholds of its own, so restoring the saved pre-override values (what
    /// <see cref="Cancel"/> does) would clobber them — and leaving the override armed would let
    /// the auto-revert clobber them later, at the next full charge. No-op when not active.
    /// </summary>
    public static void Deactivate()
    {
        if (!IsActive) return;
        ClearOverrideState();
    }

    /// <summary>
    /// The state-clearing half of a deactivate/revert: drop the persisted override flag and the
    /// saved revert thresholds, then fire <see cref="StateChanged"/>. Shared by <see cref="Deactivate"/>
    /// (clear only) and <see cref="ApplyRevert"/> (restore thresholds THEN clear) so a future added
    /// override field can't be cleared in one place and forgotten in the other.
    /// </summary>
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

    /// <summary>
    /// Fed the latest battery state by <c>App</c> on every report. Reverts to the saved thresholds
    /// once the override is active and charging has completed. "Complete" is either:
    /// <list type="bullet">
    /// <item>the Charging→Idle edge — fires at whatever level the firmware calls "full" (covers a
    /// worn battery that settles to Idle a couple of percent below 100), or</item>
    /// <item>sitting Idle at 100 % — a fallback for firmware that jumps straight to Idle without an
    /// observable Charging phase (e.g. override activated while already idle).</item>
    /// </list>
    /// The two together restore the behaviour an earlier single-trigger refactor narrowed: the
    /// edge alone missed the already-idle case, the level alone missed sub-100 % completion.
    /// </summary>
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
        // Read now, synchronously, before the async gap below — safe as a plain property read.
        // The WRITE side (the `finally` block) is what must go through Update(), since Save()
        // there is what would otherwise race a concurrent Reload() (see Update's doc comment).
        var s           = SettingsService.Current;
        var revertStart = s.TravelOverrideRevertStart;
        var revertStop  = s.TravelOverrideRevertStop;
        Task.Run(() =>
        {
            try
            {
                if (revertStart is { } start && revertStop is { } stop)
                {
                    ChargeThresholdService.SetEnabled(true);
                    ChargeThresholdService.SetThresholds(start, stop);
                }
                // If there was no threshold before the override, leave Smart Charge disabled.
            }
            catch { }
            finally
            {
                // Clear flag + saved thresholds and fire StateChanged (tooltip/menu resync).
                ClearOverrideState();
            }
        });
    }
}
