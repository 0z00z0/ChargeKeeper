using System.Runtime.InteropServices;

namespace ChargeKeeper.Vendors.Lenovo;

/// <summary>
/// Reads the connected AC adapter's rated wattage through the Lenovo Power Manager local-RPC
/// interface, via the native <c>LenPower.dll</c> bridge (see <c>native/</c> at the repo root).
/// Uses the same battery-numbering convention as <see cref="LenovoChargeThreshold"/>.
/// <para>
/// Verified on a ThinkPad X1 Yoga Gen 7 (2026-07-12, via <c>native/test-wattage.ps1</c>):
/// <c>LenGetAcAdapterWattage(battery=1)</c> returns <c>rc=0, capable=1, wattage=60</c> with a 60 W
/// USB-C adapter attached — so <c>wattage</c> is the adapter's rated power in whole watts (not
/// milliwatts), and <c>capable</c> is 1 only for the real battery index (1); index 0 returns
/// <c>capable=0</c>. On battery (no adapter) the firmware reports <c>capable=0</c>, which is why a
/// zero/incapable reading is mapped to "unknown" (null) rather than shown as "0 W".
/// </para>
/// </summary>
internal sealed class LenovoChargerInfo : IChargerInfoProvider
{
    private const string Dll = "LenPower.dll";
    private const int PrimaryBattery = 1;

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int LenGetAcAdapterWattage(int battery, out int capable, out int wattage);

    public int? GetRatedWattage()
    {
        try
        {
            if (LenGetAcAdapterWattage(PrimaryBattery, out int capable, out int wattage) != 0)
                return null;
            return capable != 0 && wattage > 0 ? wattage : null;
        }
        catch
        {
            // DllNotFoundException / EntryPointNotFoundException when the native bridge isn't
            // deployed, or (an already-deployed LenPower.dll built before this export existed)
            // when the entry point is simply missing — gracefully degrade to "unknown" instead
            // of crashing. Resolves once a rebuilt LenPower.dll (native\build.cmd) ships it.
            return null;
        }
    }
}
