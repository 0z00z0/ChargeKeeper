using System.ComponentModel;
using System.Runtime.InteropServices;
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
            using var svc = new ServiceController(ServiceName);

            // Startup type first, so a reboot mid-operation still lands in the intended state.
            SetStartupType(enable ? ServiceStartMode.Automatic : ServiceStartMode.Disabled);

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

    /// <summary>Through the service control manager: a registry write is not seen until the next boot.</summary>
    private static void SetStartupType(ServiceStartMode mode)
    {
        // ServiceController.ServiceHandle carries query rights only, so the handle is opened here.
        IntPtr manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            IntPtr service = OpenService(manager, ServiceName, ServiceChangeConfig);
            if (service == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (!ChangeServiceConfig(service, ServiceNoChange, (uint)mode, ServiceNoChange,
                        null, null, IntPtr.Zero, null, null, null, null))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    private const uint ScManagerConnect    = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceNoChange     = 0xFFFFFFFF;

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint access);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr manager, string serviceName, uint access);

    [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ChangeServiceConfig(IntPtr service, uint serviceType, uint startType,
        uint errorControl, string? binaryPathName, string? loadOrderGroup, IntPtr tagId,
        string? dependencies, string? serviceStartName, string? password, string? displayName);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

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
