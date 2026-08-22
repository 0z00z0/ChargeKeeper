namespace ChargeKeeper.Vendors;

/// <summary>
/// Reads static information about the attached AC adapter through a vendor-specific mechanism.
/// <c>null</c> means unavailable, the same convention as the rest of this namespace.
/// </summary>
public interface IChargerInfoProvider
{
    /// <summary>The connected AC adapter's rated wattage, or <c>null</c> if unknown/unavailable.</summary>
    int? GetRatedWattage();
}
