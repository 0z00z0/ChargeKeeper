namespace ChargeKeeper.Vendors.Surface;

/// <summary>
/// Microsoft Surface's power-management integration: charge limiting via the "Battery Limit" UEFI
/// setting, in two modes, with no standby scheduling and no adapter wattage. Registered last in
/// <c>VendorCatalog</c>. Inert until <see cref="ISurfaceBatteryLimitTransport"/> has a real
/// implementation: the stub transport makes <see cref="IChargeThresholdProvider.Read"/> return
/// null on every machine, so VendorCatalog skips this module.
/// </summary>
public sealed class SurfacePowerModule : IVendorPowerModule
{
    public string VendorName => "Surface";

    public IChargeThresholdProvider ChargeThreshold { get; } = new SurfaceChargeThreshold();

    public IStandbyProvider Standby { get; } = new SurfaceStandby();

    public IChargerInfoProvider ChargerInfo { get; } = new SurfaceChargerInfo();
}
