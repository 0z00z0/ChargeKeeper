using ChargeKeeper.Helpers;
using ChargeKeeper.Services;

namespace ChargeKeeper.Features;

/// <summary>Smart Charge battery threshold via the Lenovo Power Manager RPC interface.</summary>
internal sealed class SmartChargeFeature : IToggleFeature
{
    public string Name        => "Smart Charge";
    public bool   IsAvailable => ChargeThresholdService.Read()?.Capable ?? false;
    public bool   IsEnabled   => ChargeThresholdService.Read()?.Enabled ?? false;
    public bool   SetEnabled(bool enabled) => ChargeThresholdService.SetEnabled(enabled);
}

/// <summary>Smart Standby scheduling, backed by the <c>LenovoSmartStandby</c> Windows service.</summary>
internal sealed class SmartStandbyFeature : IToggleFeature
{
    public string Name        => "Smart Standby";
    public bool   IsAvailable => true; // service is always present on ThinkPads
    public bool   IsEnabled   => StandbyService.IsRunning();
    public bool   SetEnabled(bool enabled) { StandbyService.SetEnabled(enabled); return true; }
}

/// <summary>Launch-at-logon, backed by a Task Scheduler entry (UAC-free elevated start).</summary>
internal sealed class AutoStartFeature : IToggleFeature
{
    public string Name        => "Launch at startup";
    public bool   IsAvailable => true;
    public bool   IsEnabled   => TaskSchedulerHelper.IsAutoStartEnabled();
    public bool   SetEnabled(bool enabled) { TaskSchedulerHelper.SetAutoStart(enabled); return true; }
}
