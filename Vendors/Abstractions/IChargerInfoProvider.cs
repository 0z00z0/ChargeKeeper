namespace ChargeKeeper.Vendors;

/// <summary>
/// Reads static information about the currently attached AC adapter through a vendor-specific
/// mechanism. <see cref="GetRatedWattage"/> returns <c>null</c> when unavailable (no adapter
/// attached, unsupported hardware, driver missing, transport error) — same "null means
/// unavailable" convention as <see cref="IChargeThresholdProvider.Read"/>.
/// </summary>
public interface IChargerInfoProvider
{
    /// <summary>The connected AC adapter's rated wattage, or <c>null</c> if unknown/unavailable.</summary>
    int? GetRatedWattage();
}
