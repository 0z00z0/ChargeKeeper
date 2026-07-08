using System.ServiceProcess;
using Microsoft.Win32;

namespace ChargeKeeper.Services;

/// <summary>
/// Controls the <c>LenovoSmartStandby</c> Windows service, which schedules when
/// Modern Standby (S0 Low Power Idle) is active based on learned usage patterns.
/// Requires administrator privileges to start/stop or change the startup type.
/// </summary>
internal static class StandbyService
{
    private const string ServiceName  = "LenovoSmartStandby";
    private const string RegistryPath = @"SYSTEM\CurrentControlSet\Services\LenovoSmartStandby";

    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Returns <c>true</c> when the service is currently running.</summary>
    internal static bool IsRunning()
    {
        try
        {
            using var svc = new ServiceController(ServiceName);
            return svc.Status == ServiceControllerStatus.Running;
        }
        catch { return false; }
    }

    /// <summary>
    /// Starts or stops the Smart Standby service and persists the chosen startup type
    /// to the registry so the setting survives reboots.
    /// </summary>
    internal static bool SetEnabled(bool enable)
    {
        try
        {
            // Write startup type before touching the service so a reboot mid-operation
            // still lands in the intended state.
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

    // ── Private ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the service start type directly to the registry.
    /// <see cref="ServiceController"/> has no managed API for changing startup type.
    /// </summary>
    private static void PersistStartupType(ServiceStartMode mode)
    {
        using var key = Registry.LocalMachine.OpenSubKey(RegistryPath, writable: true);
        // Key is absent only when the Lenovo driver is not installed — skip silently.
        key?.SetValue("Start", (int)mode, RegistryValueKind.DWord);
    }
}
