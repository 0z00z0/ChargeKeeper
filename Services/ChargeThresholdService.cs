namespace ChargeKeeper.Services;

/// <summary>Static facade over the active vendor's <see cref="Vendors.IChargeThresholdProvider"/>
/// (see <see cref="VendorCatalog"/>), so services and UI never name a vendor.</summary>
internal static class ChargeThresholdService
{
    /// <summary>False on HP, which offers only coarse modes, so the UI hides the percentage picker
    /// instead of accepting an unappliable value.</summary>
    internal static bool SupportsNumericThresholds =>
        VendorCatalog.Active.ChargeThreshold.SupportsNumericThresholds;

    internal static ChargeThresholdState? Read() =>
        VendorCatalog.Active.ChargeThreshold.Read();

    internal static bool SetEnabled(bool enable) =>
        VendorCatalog.Active.ChargeThreshold.SetEnabled(enable);

    /// <summary>The sole caller of every vendor's <c>SetThresholds</c> (see
    /// <c>LenovoChargeThreshold</c>), so it is where a thrown native exception is told apart from a
    /// clean rejection and both, plus the success case, are recorded — a silent write and a failed
    /// one otherwise look identical to the user afterwards.</summary>
    internal static bool SetThresholds(int start, int stop, ActionCause cause)
    {
        try
        {
            bool ok = VendorCatalog.Active.ChargeThreshold.SetThresholds(start, stop);
            AppLog.Info((ok
                ? $"ChargeThreshold: wrote {start}/{stop}"
                : $"ChargeThreshold: write {start}/{stop} rejected by the device") + cause.Clause);
            return ok;
        }
        catch (Exception ex)
        {
            AppLog.Error($"ChargeThresholdService.SetThresholds({start}, {stop})", ex);
            return false;
        }
    }

    /// <summary>Modes offered instead of percentages — empty on Lenovo, three on HP. Mutually
    /// exclusive with <see cref="SupportsNumericThresholds"/>.</summary>
    internal static IReadOnlyList<ChargeMode> AvailableModes =>
        VendorCatalog.Active.ChargeThreshold.AvailableModes;

    /// <summary>The currently selected mode id, or null if unavailable or not mode-based.</summary>
    internal static string? ReadMode() =>
        VendorCatalog.Active.ChargeThreshold.ReadMode();

    internal static bool SetMode(string id) =>
        VendorCatalog.Active.ChargeThreshold.SetMode(id);
}
