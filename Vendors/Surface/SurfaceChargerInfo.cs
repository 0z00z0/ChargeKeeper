namespace ChargeKeeper.Vendors.Surface;

/// <summary>
/// Surface exposes no adapter rated-wattage source comparable to Lenovo's. Surface Connect and
/// USB-C PD negotiate a wattage, but nothing surfaces the rating to Windows in a documented
/// place this module can read.
///
/// <c>null</c> is the contract's "unknown/unavailable", so the UI simply omits the figure.
/// </summary>
internal sealed class SurfaceChargerInfo : IChargerInfoProvider
{
    public int? GetRatedWattage() => null;
}
