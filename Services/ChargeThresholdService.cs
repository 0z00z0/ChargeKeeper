namespace ChargeKeeper.Services;

/// <summary>
/// Static facade over the active vendor's <see cref="Vendors.IChargeThresholdProvider"/>
/// (see <see cref="VendorCatalog"/>). Exists so the vendor split didn't ripple through every
/// call site — the app's services/UI are static-call-based throughout, and the pre-split API
/// (<c>ChargeThresholdService.Read()</c> etc.) is preserved verbatim. The actual Lenovo RPC
/// implementation lives in the <c>ChargeKeeper.Vendors.Lenovo</c> project.
/// </summary>
internal static class ChargeThresholdService
{
    internal static ChargeThresholdState? Read() =>
        VendorCatalog.Active.ChargeThreshold.Read();

    internal static bool SetEnabled(bool enable) =>
        VendorCatalog.Active.ChargeThreshold.SetEnabled(enable);

    internal static bool SetThresholds(int start, int stop) =>
        VendorCatalog.Active.ChargeThreshold.SetThresholds(start, stop);
}
