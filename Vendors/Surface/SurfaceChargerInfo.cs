namespace ChargeKeeper.Vendors.Surface;

/// <summary>
/// Surface Connect and USB-C PD negotiate a wattage, but nothing exposes the rating to Windows
/// anywhere documented. <c>null</c> is the contract's "unknown", so the UI omits the figure.
/// </summary>
internal sealed class SurfaceChargerInfo : IChargerInfoProvider
{
    public int? GetRatedWattage() => null;
}
