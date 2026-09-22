namespace ChargeKeeper.Services;

/// <summary>Static facade over the active vendor's <see cref="Vendors.IStandbyProvider"/>
/// (see <see cref="VendorCatalog"/>), so services and UI never name a vendor.</summary>
internal static class StandbyService
{
    internal static bool IsSupported =>
        VendorCatalog.Active.Standby.IsSupported;

    internal static bool IsRunning() =>
        VendorCatalog.Active.Standby.IsRunning();

    /// <summary>Starts or stops the vendor's standby scheduling, and records the outcome. The line is
    /// written here rather than at each surface, so every route to the switch leaves the same
    /// entry.</summary>
    /// <param name="cause">What asked, for the power trail.</param>
    /// <returns>False where the vendor write was refused, which is the only signal it gives.</returns>
    internal static bool SetEnabled(bool enable, ActionCause cause)
    {
        bool written = VendorCatalog.Active.Standby.SetEnabled(enable);
        PowerLog.Event(written
            ? $"Smart Standby scheduling {(enable ? "enabled" : "disabled")}"
            : $"Smart Standby scheduling was NOT {(enable ? "enabled" : "disabled")} — the vendor write was refused",
            cause);
        return written;
    }
}
