namespace ChargeKeeper.Vendors.Hp;

/// <summary>
/// HP exposes no adapter rated-wattage source: its BIOS WMI surface carries battery sensors but
/// nothing describing the attached AC adapter. <c>null</c> is the contract's "unknown", so the UI
/// omits the figure.
/// </summary>
internal sealed class HpChargerInfo : IChargerInfoProvider
{
    public int? GetRatedWattage() => null;
}
