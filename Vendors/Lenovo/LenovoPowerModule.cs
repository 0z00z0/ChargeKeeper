namespace ChargeKeeper.Vendors.Lenovo;

/// <summary>
/// Lenovo's power-management integration: charge thresholds through the native
/// <c>LenPower.dll</c> bridge, standby scheduling through the <c>LenovoSmartStandby</c> service.
/// </summary>
public sealed class LenovoPowerModule : IVendorPowerModule
{
    public string VendorName => "Lenovo";

    public IChargeThresholdProvider ChargeThreshold { get; } = new LenovoChargeThreshold();

    public IStandbyProvider Standby { get; } = new LenovoStandby();

    public IChargerInfoProvider ChargerInfo { get; } = new LenovoChargerInfo();
}
