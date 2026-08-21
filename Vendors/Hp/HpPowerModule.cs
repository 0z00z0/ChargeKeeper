namespace ChargeKeeper.Vendors.Hp;

/// <summary>
/// HP's power-management integration: charge limiting via the "Battery Health Manager" BIOS
/// setting in the <c>root\HP\InstrumentedBIOS</c> WMI namespace. Registered in the app's
/// <c>VendorCatalog</c>; everything above VendorCatalog talks only to the
/// <see cref="IVendorPowerModule"/> contract.
///
/// Applies to HP's COMMERCIAL lines (EliteBook, ProBook, ZBook), which are the models that
/// ship the BIOS WMI namespace. On consumer SKUs and on non-HP hardware,
/// <see cref="IChargeThresholdProvider.Read"/> returns null and VendorCatalog moves on to the
/// next candidate.
///
/// Unlike the Lenovo module this ships NO native component — HP's surface is plain managed
/// WMI, and reads work without elevation.
///
/// Coverage is deliberately narrower than Lenovo's: charge limiting only, in three coarse
/// modes rather than a numeric range, with no standby scheduling and no adapter wattage.
/// See <see cref="HpChargeThreshold"/> for why.
/// </summary>
public sealed class HpPowerModule : IVendorPowerModule
{
    public string VendorName => "HP";

    public IChargeThresholdProvider ChargeThreshold { get; } = new HpChargeThreshold();

    public IStandbyProvider Standby { get; } = new HpStandby();

    public IChargerInfoProvider ChargerInfo { get; } = new HpChargerInfo();
}
