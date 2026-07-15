using System.Runtime.InteropServices;

namespace ChargeKeeper.Vendors.Lenovo;

/// <summary>
/// Reads and writes the battery charge start/stop thresholds through the Lenovo Power
/// Manager local-RPC interface, via the native <c>LenPower.dll</c> bridge (see
/// <c>native/</c> at the repo root). This is the same interface Lenovo Vantage uses; on
/// ThinkPad firmware the threshold is NOT exposed through <c>Lenovo_BiosSetting</c>, so WMI
/// cannot reach it.
///
/// Requires administrator privileges and the "Lenovo Power and Battery"
/// (<c>POWERMGR_COMPONENT</c>) system device to be present.
/// </summary>
internal sealed class LenovoChargeThreshold : IChargeThresholdProvider
{
    private const string Dll = "LenPower.dll";

    // Primary battery. The interface is 1-based; internal batteries are battery 1.
    private const int PrimaryBattery = 1;

    // Defaults applied when enabling without a previously-set custom range.
    private const int DefaultStart = 75;
    private const int DefaultStop  = 80;

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int LenGetChargeThreshold(
        int battery, out int capable, out int enabled, out int start, out int stop);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int LenSetChargeThreshold(int battery, int start, int stop);

    public ChargeThresholdState? Read()
    {
        try
        {
            if (LenGetChargeThreshold(PrimaryBattery, out int cap, out int en, out int start, out int stop) != 0)
                return null;

            return new(cap != 0, en != 0, start, stop);
        }
        catch
        {
            // DllNotFoundException / EntryPointNotFound when the native bridge isn't deployed.
            return null;
        }
    }

    public bool SetEnabled(bool enable)
    {
        try
        {
            if (!enable)
                return LenSetChargeThreshold(PrimaryBattery, 0, 0) == 0; // 0/0 = charge to 100%

            // Keep the user's current thresholds if both look valid; otherwise default.
            var current   = Read();
            bool useCustom = current is { Start: > 0 and <= 100, Stop: > 0 and <= 100 };
            int start = useCustom ? current!.Start : DefaultStart;
            int stop  = useCustom ? current!.Stop  : DefaultStop;
            if (start >= stop) { start = DefaultStart; stop = DefaultStop; }

            return LenSetChargeThreshold(PrimaryBattery, start, stop) == 0;
        }
        catch { return false; }
    }

    public bool SetThresholds(int start, int stop)
    {
        if (start < 1 || stop > 100 || start >= stop) return false;
        try { return LenSetChargeThreshold(PrimaryBattery, start, stop) == 0; }
        catch { return false; }
    }
}
