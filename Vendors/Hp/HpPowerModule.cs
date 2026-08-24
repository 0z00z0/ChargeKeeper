namespace ChargeKeeper.Vendors.Hp;

/// <summary>
/// HP's power-management integration: charge limiting via the "Battery Health Manager" BIOS
/// setting, on the commercial lines that ship the <c>root\HP\InstrumentedBIOS</c> WMI namespace.
/// Elsewhere <see cref="IChargeThresholdProvider.Read"/> returns null and <c>VendorCatalog</c>
/// moves on to the next candidate. No standby scheduling and no adapter wattage.
/// </summary>
public sealed class HpPowerModule : IVendorPowerModule
{
    public string VendorName => "HP";

    public IChargeThresholdProvider ChargeThreshold { get; } = new HpChargeThreshold();

    public IStandbyProvider Standby { get; } = new HpStandby();

    public IChargerInfoProvider ChargerInfo { get; } = new HpChargerInfo();
}
