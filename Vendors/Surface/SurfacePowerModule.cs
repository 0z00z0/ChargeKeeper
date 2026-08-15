namespace ChargeKeeper.Vendors.Surface;

/// <summary>
/// Microsoft Surface's power-management integration: charge limiting via the "Battery Limit"
/// UEFI setting. Registered LAST in the app's <c>VendorCatalog</c>; everything above the catalog
/// talks only to the <see cref="IVendorPowerModule"/> contract.
///
/// STATUS — structurally complete, functionally inert. The Windows-side mechanism for reading
/// and writing Battery Limit is NOT confirmed, so <see cref="SurfaceBatteryLimitApi"/> ships a
/// stub transport that always reports unavailable. <see cref="IChargeThresholdProvider.Read"/>
/// therefore returns null on every machine, the catalog skips this module, and the app behaves
/// exactly as it did before it existed. Making it live means implementing one transport class —
/// see <see cref="ISurfaceBatteryLimitTransport"/> for the candidate mechanisms.
///
/// Coverage is the narrowest of the three vendors: charge limiting only, in two modes, with no
/// standby scheduling and no adapter wattage. See <see cref="SurfaceChargeThreshold"/> for why.
/// </summary>
public sealed class SurfacePowerModule : IVendorPowerModule
{
    public string VendorName => "Surface";

    public IChargeThresholdProvider ChargeThreshold { get; } = new SurfaceChargeThreshold();

    public IStandbyProvider Standby { get; } = new SurfaceStandby();

    public IChargerInfoProvider ChargerInfo { get; } = new SurfaceChargerInfo();
}
