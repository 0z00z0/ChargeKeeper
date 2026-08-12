using ChargeKeeper.Vendors;
using ChargeKeeper.Vendors.Hp;
using ChargeKeeper.Vendors.Lenovo;

namespace ChargeKeeper.Services;

/// <summary>
/// Selects which vendor's power-management module the app drives, by probing each candidate and
/// taking the first whose <c>ChargeThreshold.Read()</c> answers. Nothing above this class
/// changes when a vendor is added: all UI/feature code reaches the hardware through the
/// <see cref="ChargeThresholdService"/> and <see cref="StandbyService"/> facades, which delegate
/// to <see cref="Active"/>.
///
/// The probe IS the availability check — <c>Read()</c> returning null already means "driver
/// missing, unsupported hardware, or transport error", so no separate capability call is needed.
/// </summary>
internal static class VendorCatalog
{
    internal static IVendorPowerModule Active { get; } = SelectActive();

    /// <summary>
    /// Probes candidates in order. A machine is realistically one vendor or neither, so order
    /// only decides a tie that should not occur; Lenovo goes first because its probe is a cheap
    /// P/Invoke that fails immediately when the native bridge is absent, whereas HP's opens a
    /// WMI namespace.
    /// </summary>
    private static IVendorPowerModule SelectActive()
    {
        IVendorPowerModule[] candidates = [new LenovoPowerModule(), new HpPowerModule()];

        foreach (var candidate in candidates)
        {
            try
            {
                if (candidate.ChargeThreshold.Read() is not null)
                    return candidate;
            }
            catch
            {
                // Providers are contractually non-throwing, but this runs inside a static
                // initializer: an escaped exception would surface as a
                // TypeInitializationException and take down app startup rather than degrading
                // to "Unavailable". Never let a probe do that.
            }
        }

        // Nothing answered — unsupported hardware, or a supported machine missing its driver.
        // Fall back to the first candidate so the app still runs and reports Unavailable,
        // which is exactly how it behaved before any probing existed.
        return candidates[0];
    }
}
