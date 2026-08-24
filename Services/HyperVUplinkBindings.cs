using System.Management;

namespace ChargeKeeper.Services;

/// <summary>
/// The adapters Windows records as carrying a Hyper-V external switch, read from
/// <c>MSFT_NetAdapterBindingSettingData</c> in <c>root\standardcimv2</c> where the switch protocol
/// <c>vms_pp</c> is bound and enabled. Authoritative where
/// <see cref="NetworkLocationService.ResolveBridgedPeer"/> can only infer: pairing a host vNIC to its
/// uplink by shared MAC relies on the switch cloning the uplink's address, which is the default but
/// can be overridden.
/// </summary>
/// <remarks>
/// Measured unelevated, with no Hyper-V PowerShell module: docked, one enabled row naming "Ethernet" /
/// "Realtek USB GbE Family Controller"; undocked, five rows all disabled, so the filtered read is
/// empty. <c>root\virtualization\v2</c> is not a substitute — <c>Msvm_ExternalEthernetPort</c> and
/// <c>Msvm_VirtualEthernetSwitch</c> return zero rows unelevated, silently rather than with an access
/// error. The read costs 300-650 ms even when it returns nothing, so
/// <see cref="NetworkLocationService.DetectDetailed"/> pays it only when a switch port is actually
/// among the candidates.
/// </remarks>
internal static class HyperVUplinkBindings
{
    // Generous against the measured 300-650 ms the provider takes with nothing to return, tight enough
    // to bound a wedged WMI service. Overrunning it costs the preferred path, never the reading: the MAC
    // walk-back is the documented fallback.
    private const int TimeoutMs = 3000;

    // Detection re-evaluates on every network change, so a permanently broken WMI would otherwise
    // write a line per evaluation.
    private static int _failureLogged;

    /// <summary>The enabled <c>vms_pp</c> bindings, or an empty list when the read finds none, fails
    /// or overruns its time box. Never throws.</summary>
    internal static IReadOnlyList<UplinkBinding> Read()
    {
        try
        {
            var read = Task.Run(Query);
            if (read.Wait(TimeoutMs)) return read.Result;

            // The task is left running rather than cancelled — ManagementObjectSearcher offers no
            // cancellation — but it can no longer fault unobserved: Query swallows its own failure.
            LogOnce($"HyperVUplinkBindings: the vms_pp read exceeded {TimeoutMs} ms", null);
            return [];
        }
        catch (Exception ex)
        {
            LogOnce("HyperVUplinkBindings.Read", ex);
            return [];
        }
    }

    // On a pool thread, so Read can time-box it.
    private static IReadOnlyList<UplinkBinding> Query()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"root\standardcimv2"),
                new ObjectQuery("SELECT Name, InterfaceDescription FROM MSFT_NetAdapterBindingSettingData "
                                + "WHERE ComponentID = 'vms_pp' AND Enabled = TRUE"));

            var bindings = new List<UplinkBinding>();
            using var rows = searcher.Get();
            foreach (ManagementBaseObject row in rows)
                using (row)
                    bindings.Add(new UplinkBinding(row["Name"] as string ?? "",
                                                   row["InterfaceDescription"] as string ?? ""));
            return bindings;
        }
        catch (Exception ex)
        {
            LogOnce("HyperVUplinkBindings.Query", ex);
            return [];
        }
    }

    private static void LogOnce(string source, Exception? ex)
    {
        if (System.Threading.Interlocked.Exchange(ref _failureLogged, 1) == 0) AppLog.Error(source, ex);
    }
}
