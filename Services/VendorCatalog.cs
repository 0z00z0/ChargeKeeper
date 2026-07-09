using ChargeKeeper.Vendors;
using ChargeKeeper.Vendors.Lenovo;

namespace ChargeKeeper.Services;

/// <summary>
/// Selects which vendor's power-management module the app drives. Lenovo is the only module
/// today, so selection is trivial; when another vendor lands (e.g. a future
/// <c>ChargeKeeper.Vendors.Hp</c>), probe each candidate here — e.g. pick the first whose
/// <c>ChargeThreshold.Read()</c> returns non-null — and nothing above this class changes:
/// all UI/feature code reaches the hardware through the <see cref="ChargeThresholdService"/>
/// and <see cref="StandbyService"/> facades, which delegate to <see cref="Active"/>.
/// </summary>
internal static class VendorCatalog
{
    internal static IVendorPowerModule Active { get; } = new LenovoPowerModule();
}
