namespace ChargeKeeper.Services;

/// <summary>
/// Static facade over the active vendor's <see cref="Vendors.IStandbyProvider"/>
/// (see <see cref="VendorCatalog"/>). Preserves the pre-split static API so call sites didn't
/// change; the actual <c>LenovoSmartStandby</c> service control lives in the
/// <c>ChargeKeeper.Vendors.Lenovo</c> project.
/// </summary>
internal static class StandbyService
{
    internal static bool IsRunning() =>
        VendorCatalog.Active.Standby.IsRunning();

    internal static bool SetEnabled(bool enable) =>
        VendorCatalog.Active.Standby.SetEnabled(enable);
}
