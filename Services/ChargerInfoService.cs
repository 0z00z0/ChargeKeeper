namespace ChargeKeeper.Services;

/// <summary>
/// Static facade over the active vendor's <see cref="Vendors.IChargerInfoProvider"/>
/// (see <see cref="VendorCatalog"/>), mirroring <see cref="ChargeThresholdService"/>.
/// </summary>
internal static class ChargerInfoService
{
    internal static int? GetRatedWattage() => VendorCatalog.Active.ChargerInfo.GetRatedWattage();
}
