namespace ChargeKeeper.Vendors.Surface;

/// <summary>
/// Surface has no equivalent to Lenovo's <c>LenovoSmartStandby</c> service: Modern Standby here is
/// governed by Windows' own power policy, with nothing vendor-specific to toggle. Reporting
/// not-supported is what hides the Smart Standby toggle on Surface.
/// </summary>
internal sealed class SurfaceStandby : IStandbyProvider
{
    public bool IsSupported => false;

    public bool IsRunning() => false;

    public bool SetEnabled(bool enable) => false;
}
