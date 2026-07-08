using System.Runtime.InteropServices;

namespace ChargeKeeper.Services;

/// <summary>
/// Current battery charge-threshold configuration, as reported by the Lenovo Power
/// Manager. <see cref="Enabled"/> is false when the battery charges to 100% (no threshold).
/// </summary>
internal sealed record ChargeThresholdState(bool Capable, bool Enabled, int Start, int Stop);

/// <summary>
/// Reads and writes the battery charge start/stop thresholds through the Lenovo Power
/// Manager local-RPC interface, via the native <c>LenPower.dll</c> bridge (see
/// <c>native/</c>). This is the same interface Lenovo Vantage uses; on ThinkPad firmware
/// the threshold is NOT exposed through <c>Lenovo_BiosSetting</c>, so WMI cannot reach it.
///
/// Requires administrator privileges and the "Lenovo Power and Battery"
/// (<c>POWERMGR_COMPONENT</c>) system device to be present.
/// </summary>
internal static class ChargeThresholdService
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

    /// <summary>
    /// Reads the current threshold state, or <c>null</c> if the interface is unavailable
    /// (driver missing, DLL absent, or RPC error).
    /// </summary>
    internal static ChargeThresholdState? Read()
    {
        try
        {
            if (LenGetChargeThreshold(PrimaryBattery, out int cap, out int en, out int start, out int stop) != 0)
                return null;

            return new ChargeThresholdState(cap != 0, en != 0, start, stop);
        }
        catch
        {
            // DllNotFoundException / EntryPointNotFound when the native bridge isn't deployed.
            return null;
        }
    }

    /// <summary>
    /// Enables the charge threshold (preserving any existing custom range, else applying
    /// sensible defaults) or disables it so the battery charges to 100%.
    /// </summary>
    internal static bool SetEnabled(bool enable)
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

    /// <summary>
    /// Writes explicit start/stop thresholds (1–100, start &lt; stop).
    /// Returns <c>false</c> and makes no RPC call when the arguments are out of range.
    /// </summary>
    internal static bool SetThresholds(int start, int stop)
    {
        if (start < 1 || stop > 100 || start >= stop) return false;
        try { return LenSetChargeThreshold(PrimaryBattery, start, stop) == 0; }
        catch { return false; }
    }
}
