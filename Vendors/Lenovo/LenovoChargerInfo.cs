using System.Runtime.InteropServices;

namespace ChargeKeeper.Vendors.Lenovo;

/// <summary>
/// Reads the connected AC adapter's rated wattage through the Lenovo Power Manager local-RPC
/// interface, via the native <c>LenPower.dll</c> bridge. The firmware reports whole watts, and
/// sets <c>capable</c> to 0 when no adapter is attached — hence a zero or incapable reading
/// becomes "unknown" rather than "0 W".
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
            // Native bridge not deployed, or the export missing from it — degrade to "unknown".
            return null;
        }
    }
}
