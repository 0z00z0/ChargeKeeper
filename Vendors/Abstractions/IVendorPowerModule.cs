namespace ChargeKeeper.Vendors;

/// <summary>
/// One laptop vendor's power-management integration (charge thresholds + standby control).
/// Each vendor lives in its own assembly (e.g. <c>ChargeKeeper.Vendors.Lenovo</c>) implementing
/// this contract, so support for another vendor (HP, Dell, …) is a new project plus a one-line
/// registration in the app's <c>VendorCatalog</c> — no changes to the UI or feature code, which
/// only ever talk to these interfaces.
/// </summary>
public interface IVendorPowerModule
{
    /// <summary>Vendor display name, e.g. "Lenovo".</summary>
    string VendorName { get; }

    /// <summary>Battery charge start/stop threshold control.</summary>
    IChargeThresholdProvider ChargeThreshold { get; }

    /// <summary>Modern-Standby scheduling control.</summary>
    IStandbyProvider Standby { get; }
}
