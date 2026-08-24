using System.ServiceProcess;
using Microsoft.Win32;

namespace ChargeKeeper.Vendors.Lenovo;

/// <summary>
/// Controls the <c>LenovoSmartStandby</c> Windows service, which schedules when Modern Standby
/// (S0 Low Power Idle) is active based on learned usage patterns. Starting, stopping or changing
/// the startup type requires elevation.
/// </summary>
internal sealed class LenovoStandby : IStandbyProvider
{
    private const string ServiceName  = "LenovoSmartStandby";
    private const string RegistryPath = @"SYSTEM\CurrentControlSet\Services\LenovoSmartStandby";

    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Whether the Lenovo power stack is installed. This probes rather than asserts because
    /// <c>VendorCatalog</c> falls back to this module when no vendor answers, so it is also the
    /// active module on non-Lenovo hardware.
    /// </summary>
    public bool IsSupported => ServiceIsInstalled();

    public bool IsRunning()
    {
        try
        {
            using var svc = new ServiceController(ServiceName);
            return svc.Status == ServiceControllerStatus.Running;
        }
        catch { return false; }
    }

    public bool SetEnabled(bool enable)
    {
        try
        {
            // Startup type first, so a reboot mid-operation still lands in the intended state.
            PersistStartupType(enable ? ServiceStartMode.Automatic : ServiceStartMode.Disabled);

            using var svc = new ServiceController(ServiceName);

            if (enable && svc.Status != ServiceControllerStatus.Running)
            {
                svc.Start();
                svc.WaitForStatus(ServiceControllerStatus.Running, StatusTimeout);
            }
            else if (!enable && svc.Status == ServiceControllerStatus.Running)
            {
                svc.Stop();
                svc.WaitForStatus(ServiceControllerStatus.Stopped, StatusTimeout);
            }

            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Writes the service start type straight to the registry, because
    /// <see cref="ServiceController"/> has no managed API for changing it.
    /// </summary>
    private static void PersistStartupType(ServiceStartMode mode)
    {
        using var key = Registry.LocalMachine.OpenSubKey(RegistryPath, writable: true);
        // Key is absent only when the Lenovo driver is not installed — skip silently.
        key?.SetValue("Start", (int)mode, RegistryValueKind.DWord);
    }

    /// <summary>The service's own registry key exists only where the Lenovo driver is installed.</summary>
    private static bool ServiceIsInstalled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryPath);
            return key is not null;
        }
        catch { return false; }
    }
}
