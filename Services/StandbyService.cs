namespace ChargeKeeper.Services;

/// <summary>Static facade over the active vendor's <see cref="Vendors.IStandbyProvider"/>
/// (see <see cref="VendorCatalog"/>), so services and UI never name a vendor.</summary>
internal static class StandbyService
{
    internal static bool IsSupported =>
        VendorCatalog.Active.Standby.IsSupported;

    internal static bool IsRunning() =>
        VendorCatalog.Active.Standby.IsRunning();

    internal static bool SetEnabled(bool enable) =>
        VendorCatalog.Active.Standby.SetEnabled(enable);
}
