using ChargeKeeper.Helpers;
using ChargeKeeper.Services;

namespace ChargeKeeper.Features;

/// <summary>Launch-at-logon, backed by a Task Scheduler entry (UAC-free elevated start).</summary>
internal sealed class AutoStartFeature : IToggleFeature
{
    public string Name        => "Launch at startup";
    public bool   IsAvailable => true;
    public bool   IsEnabled   => TaskSchedulerHelper.IsAutoStartEnabled();
    public bool   SetEnabled(bool enabled) { TaskSchedulerHelper.SetAutoStart(enabled); return true; }
}

/// <summary>
/// Keep the machine awake. Switching on applies the first configured preset: a tray toggle is one
/// click, and picking a different span is a Settings or dashboard job.
/// </summary>
internal sealed class KeepAwakeFeature : IToggleFeature
{
    public string Name        => "Keep awake";
    public bool   IsAvailable => true;
    public bool   IsEnabled   => KeepAwakeService.Current is not null;

    public bool SetEnabled(bool enabled)
    {
        if (!enabled) { KeepAwakeService.Deactivate(); return true; }
        // Shared with the dashboard badge's switch so both default to the same span.
        KeepAwakeService.Activate(KeepAwakePolicy.DefaultRequest(SettingsService.Current.KeepAwakePresets));
        return true;
    }
}
