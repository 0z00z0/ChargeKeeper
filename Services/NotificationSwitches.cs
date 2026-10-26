namespace ChargeKeeper.Services;

/// <summary>The on/off switch each notification has on the Notifications page, read and set by
/// kind so the page and the notification path cannot pair a kind with a different switch.</summary>
internal static class NotificationSwitches
{
    public static bool IsOn(AppSettings settings, NotificationKind kind) => kind switch
    {
        NotificationKind.LowBattery       => settings.LowBatteryWarningEnabled,
        NotificationKind.HighBattery      => settings.HighBatteryWarningEnabled,
        NotificationKind.DrainAnomaly     => settings.DrainAnomalyWarningEnabled,
        NotificationKind.ChargeComplete   => settings.ChargeCompleteNoticeEnabled,
        NotificationKind.ChargingStarted  => settings.ChargingStartedNoticeEnabled,
        NotificationKind.SleptWhileHot    => settings.SleptWhileHotWarningEnabled,
        NotificationKind.SettingsNotSaved => settings.SettingsNotSavedWarningEnabled,
        NotificationKind.ScriptFailed     => settings.ScriptFailedWarningEnabled,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "a notification with no switch"),
    };

    public static void Set(AppSettings settings, NotificationKind kind, bool on)
    {
        switch (kind)
        {
            case NotificationKind.LowBattery:       settings.LowBatteryWarningEnabled       = on; break;
            case NotificationKind.HighBattery:      settings.HighBatteryWarningEnabled      = on; break;
            case NotificationKind.DrainAnomaly:     settings.DrainAnomalyWarningEnabled     = on; break;
            case NotificationKind.ChargeComplete:   settings.ChargeCompleteNoticeEnabled    = on; break;
            case NotificationKind.ChargingStarted:  settings.ChargingStartedNoticeEnabled   = on; break;
            case NotificationKind.SleptWhileHot:    settings.SleptWhileHotWarningEnabled    = on; break;
            case NotificationKind.SettingsNotSaved: settings.SettingsNotSavedWarningEnabled = on; break;
            case NotificationKind.ScriptFailed:     settings.ScriptFailedWarningEnabled     = on; break;
            default: throw new ArgumentOutOfRangeException(nameof(kind), kind, "a notification with no switch");
        }
    }
}
