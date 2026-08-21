namespace ChargeKeeper.Vendors;

/// <summary>
/// Controls the vendor's smart-standby scheduling (when Modern Standby / S0 Low Power Idle is
/// allowed to engage). All methods are best-effort and must not throw: a missing driver or
/// service reports not-supported / not-running / failure.
/// </summary>
public interface IStandbyProvider
{
    /// <summary>
    /// Whether standby scheduling exists on this machine. Distinct from <see cref="IsRunning"/>:
    /// not-supported hides the toggle, whereas supported-but-off shows it unchecked.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>Whether the vendor's standby-scheduling component is currently active.</summary>
    bool IsRunning();

    /// <summary>Enables or disables standby scheduling, persisting across reboots.</summary>
    bool SetEnabled(bool enable);
}
