namespace ChargeKeeper.Vendors;

/// <summary>
/// Controls the vendor's smart-standby scheduling (when Modern Standby / S0 Low Power Idle is
/// allowed to engage). All methods are best-effort and must not throw — a missing driver or
/// service simply reports not-running / failure, matching how the rest of the app degrades on
/// non-supported hardware.
/// </summary>
public interface IStandbyProvider
{
    /// <summary>
    /// Whether this vendor implements standby scheduling at all.
    ///
    /// Distinct from <see cref="IsRunning"/>: "supported but currently off" is a togglable
    /// state, whereas "not supported" means the feature should not be offered. Lenovo backs
    /// this with the <c>LenovoSmartStandby</c> service; HP has no equivalent, so the toggle
    /// must be hidden rather than shown enabled and doing nothing.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>Whether the vendor's standby-scheduling component is currently active.</summary>
    bool IsRunning();

    /// <summary>Enables or disables standby scheduling, persisting across reboots.</summary>
    bool SetEnabled(bool enable);
}
