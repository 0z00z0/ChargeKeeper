using ChargeKeeper.Vendors;
using ChargeKeeper.Vendors.Hp;
using ChargeKeeper.Vendors.Lenovo;
using ChargeKeeper.Vendors.Surface;

namespace ChargeKeeper.Services;

/// <summary>Selects which vendor's power-management module the app drives, taking the first candidate
/// whose <c>ChargeThreshold.Read()</c> answers — a null read already means "driver missing, unsupported
/// hardware, or transport error", so no separate capability call is needed.</summary>
internal static class VendorCatalog
{
    internal static IVendorPowerModule Active { get; } = SelectActive();

    /// <summary>Order only decides a tie that should not occur — a machine is one vendor or neither.
    /// Lenovo first (cheap P/Invoke; HP opens a WMI namespace), Surface last while its transport is a
    /// stub that always returns null.</summary>
    private static IVendorPowerModule SelectActive()
        => SelectFrom([new LenovoPowerModule(), new HpPowerModule(), new SurfacePowerModule()]);

    /// <summary>Split out from <see cref="SelectActive"/> so the "a throwing probe must not escape"
    /// guarantee is testable without launching the app.</summary>
    internal static IVendorPowerModule SelectFrom(IReadOnlyList<IVendorPowerModule> candidates)
    {
        foreach (var candidate in candidates)
        {
            try
            {
                if (candidate.ChargeThreshold.Read() is not null)
                    return candidate;
            }
            catch
            {
                // Runs inside a static initializer: an escaped exception would surface as a
                // TypeInitializationException and take down startup instead of degrading to
                // "Unavailable".
            }
        }

        // Nothing answered. Fall back to the first candidate so the app still runs and reports
        // Unavailable.
        return candidates[0];
    }
}
