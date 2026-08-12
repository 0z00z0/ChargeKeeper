namespace ChargeKeeper.Vendors.Hp;

/// <summary>
/// HP exposes no adapter rated-wattage source comparable to Lenovo's. The BIOS WMI surface
/// carries battery sensors (<c>HP_BIOSNumericSensor</c> reports battery temperature) but
/// nothing describing the attached AC adapter.
///
/// <c>null</c> is the contract's "unknown/unavailable", so the UI simply omits the figure.
/// </summary>
internal sealed class HpChargerInfo : IChargerInfoProvider
{
    public int? GetRatedWattage() => null;
}
