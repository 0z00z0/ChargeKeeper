namespace ChargeKeeper.Vendors.Lenovo;

/// <summary>
/// Lenovo's power-management integration: charge thresholds via the Lenovo Power Manager
/// local-RPC interface (through the native <c>LenPower.dll</c> bridge) and standby scheduling
/// via the <c>LenovoSmartStandby</c> Windows service. Registered in the app's
/// <c>VendorCatalog</c>; everything above the catalog talks only to the
/// <see cref="IVendorPowerModule"/> contract.
/// </summary>
public sealed class LenovoPowerModule : IVendorPowerModule
{
    public string VendorName => "Lenovo";

    public IChargeThresholdProvider ChargeThreshold { get; } = new LenovoChargeThreshold();

    public IStandbyProvider Standby { get; } = new LenovoStandby();
}
