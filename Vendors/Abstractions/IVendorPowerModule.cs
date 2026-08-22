namespace ChargeKeeper.Vendors;

/// <summary>
/// One laptop vendor's power-management integration. Adding a vendor is an implementation of this
/// contract plus a one-line registration in <c>VendorCatalog</c>; nothing above it names a vendor.
/// </summary>
public interface IVendorPowerModule
{
    /// <summary>Vendor display name, e.g. "Lenovo".</summary>
    string VendorName { get; }

    /// <summary>Battery charge start/stop threshold control.</summary>
    IChargeThresholdProvider ChargeThreshold { get; }

    /// <summary>Modern-Standby scheduling control.</summary>
    IStandbyProvider Standby { get; }

    /// <summary>Connected AC adapter information (e.g. rated wattage).</summary>
    IChargerInfoProvider ChargerInfo { get; }
}
