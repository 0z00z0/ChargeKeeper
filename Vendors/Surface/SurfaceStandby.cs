namespace ChargeKeeper.Vendors.Surface;

/// <summary>
/// Standby scheduling is Lenovo-only. Surface has no equivalent to the
/// <c>LenovoSmartStandby</c> service — Modern Standby on Surface is governed by Windows' own
/// power policy, with nothing vendor-specific to toggle.
///
/// Reporting not-supported / not-running / write-failed is the contract's documented way to say
/// "this vendor does not have this", so no exception is thrown and no capability is faked.
///
/// <see cref="IsSupported"/> is what hides the Smart Standby toggle, so it is not offered on
/// Surface rather than rendering enabled and silently doing nothing.
/// </summary>
internal sealed class SurfaceStandby : IStandbyProvider
{
    public bool IsSupported => false;

    public bool IsRunning() => false;

    public bool SetEnabled(bool enable) => false;
}
