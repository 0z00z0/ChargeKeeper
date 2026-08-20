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

    // Both flags come from a single Power-Manager RPC read — override ReadState so the menu's
    // snapshot pays one round-trip, not the two that IsAvailable + IsEnabled would each cost.
    public (bool Available, bool Enabled) ReadState()
    {
        var s = ChargeThresholdService.Read();
        return (s?.Capable ?? false, s?.Enabled ?? false);
    }
}

/// <summary>Smart Standby scheduling, backed by the <c>LenovoSmartStandby</c> Windows service.</summary>
internal sealed class SmartStandbyFeature : IToggleFeature
{
    public string Name        => "Smart Standby";
    // Vendor-dependent, not universal: this was hardcoded true on the assumption that the
    // service always ships on ThinkPads, which stopped being safe once HP joined — HP has no
    // standby-scheduling equivalent, and a toggle that renders enabled and silently does
    // nothing is worse than one that isn't offered.
    public bool   IsAvailable => StandbyService.IsSupported;
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

/// <summary>
/// Keep the machine awake (issue #90). On applies the FIRST configured preset — the whole point of a
/// tray toggle is one click, and picking a different span is a Settings/dashboard job — so the call
/// site stays a single <see cref="IToggleFeature"/> like every other toggle.
/// </summary>
internal sealed class KeepAwakeFeature : IToggleFeature
{
    public string Name        => "Keep awake";
    public bool   IsAvailable => true;
    public bool   IsEnabled   => KeepAwakeService.Current is not null;

    public bool SetEnabled(bool enabled)
    {
        if (!enabled) { KeepAwakeService.Deactivate(); return true; }
        // Shared with the dashboard badge's switch, so the two "on with no span picked" surfaces
        // cannot pick different spans.
        KeepAwakeService.Activate(KeepAwakePolicy.DefaultRequest(SettingsService.Current.KeepAwakePresets));
        return true;
    }
}
