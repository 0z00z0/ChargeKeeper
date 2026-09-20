using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace ChargeKeeper.Services;

internal static class ToastService
{
    private static bool _registered;

    /// <summary>False until Windows accepts the registration. Every warning raised while it is
    /// false is a warning nobody will see, and the log has to be able to say so.</summary>
    public static bool IsAvailable => _registered;

    public static void Register()
    {
        if (_registered)
            return;

        try
        {
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch (Exception ex)
        {
            // Not swallowed: a refused registration used to make every later warning vanish with
            // nothing anywhere to say why. The readable line first, the detail behind it.
            AppLog.Info(NotificationMessages.Unavailable);
            AppLog.Error("ToastService.Register", ex);
        }
    }

    // Every notification is fire-and-forget and must never crash the app, so the build+show+report
    // scaffold lives here once.
    private static void TryShow(NotificationKind kind, int? atPercent, string title, string body)
    {
        var (switchedOn, sound) =
            SettingsService.Read(s => (NotificationSwitches.IsOn(s, kind), s.NotificationSound));

        // Said in the log, so a notification switched off is not mistaken for one Windows refused.
        if (!switchedOn)
        {
            AppLog.Info(NotificationMessages.SwitchedOff(kind, atPercent));
            return;
        }

        try
        {
            var builder = new AppNotificationBuilder()
                .AddText(title)
                .AddText(body);

            if (NotificationSounds.SilencesWindowsAudio(sound))
                builder.MuteAudio();

            AppNotificationManager.Default.Show(builder.BuildNotification());
            AppLog.Info(NotificationMessages.Shown(kind, atPercent));

            // After the show, so a refused notification plays nothing.
            NotificationSoundPlayer.PlayFor(kind, sound);
        }
        catch (Exception ex)
        {
            AppLog.Info(NotificationMessages.CouldNotBeShown(kind, atPercent, ex.Message));
            AppLog.Error("ToastService.Show", ex);
        }
    }

    public static void NotifyChargeComplete(int stopPct) =>
        TryShow(NotificationKind.ChargeComplete, stopPct, "Battery charged", stopPct == 100
            ? "Fully charged"
            : $"Smart Charge stopped at {stopPct}%  —  charged to limit");

    public static void NotifyChargingStarted() =>
        TryShow(NotificationKind.ChargingStarted, null, "Charging", "AC power connected");

    public static void NotifyLowBattery(int pct) =>
        TryShow(NotificationKind.LowBattery, pct, "Low battery", $"Battery at {pct}% — connect AC power");

    public static void NotifyHighBattery(int pct, int warnAtPct) =>
        TryShow(NotificationKind.HighBattery, pct, "High battery",
                $"Battery at {pct}% — above the {warnAtPct}% warning level");

    /// <summary><paramref name="dropPercent"/> is always positive — the caller filters rises and flats.</summary>
    public static void NotifyDrainAnomaly(int dropPercent, TimeSpan duration)
    {
        string span = duration.TotalHours >= 1 ? $"{duration.TotalHours:0.#}h" : $"{duration.Minutes}m";
        TryShow(NotificationKind.DrainAnomaly, null, "Unusual battery drain",
                $"Lost {dropPercent}% over {span} while asleep — Modern Standby misbehaving?");
    }

    /// <summary>
    /// Said at the next wake rather than when it happened: nobody sees a notification inside a
    /// closed bag. The wording states the fact and the reading, so the event is not mistaken for a
    /// crash, a flat battery or a lid-close wait that failed.
    /// </summary>
    public static void NotifySleptWhileHot(double celsius, DateTimeOffset atUtc)
    {
        string when = atUtc.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture);
        TryShow(NotificationKind.SleptWhileHot, null, "Slept early to cool down",
                $"Reached {celsius:0.#} °C with the lid shut at {when}, so the lid-close wait ended and the computer slept.");
    }

    /// <summary>
    /// Said as it happens, because the setting on screen and the setting on disk have parted and
    /// nothing else shows it: the page keeps the new value, the next start comes back with the old
    /// one. The store returns a refused write rather than raising it, so without this the change
    /// simply disappears.
    /// </summary>
    public static void NotifySettingsNotSaved() =>
        TryShow(NotificationKind.SettingsNotSaved, null, "Settings not saved",
                "The settings file could not be written, so the last change is not stored and will "
              + "be gone at the next start. The application log says why.");

    /// <summary>
    /// Said the first time a script fails and not again until one of its runs succeeds. The latch is
    /// on the runner, which owns the failure; without the latch a script bound to the charger would
    /// warn on every plug and unplug for as long as it stayed broken.
    /// </summary>
    public static void NotifyScriptFailed(string script, string reason) =>
        TryShow(NotificationKind.ScriptFailed, null, "Script failed",
                ScriptMessages.FailureNotice(script, reason));

    /// <summary>
    /// Said once per hold, and not again until that holder lets go: a program that has held the
    /// machine awake for hours is one fact, not one per reading.
    /// </summary>
    public static void NotifyAwakeHold(string holder, TimeSpan held) =>
        TryShow(NotificationKind.AwakeHold, null, "Something is keeping this computer awake",
                $"{holder} has been asking Windows to stay awake for {SleepWatch.Duration(held)}. "
              + "The dashboard lists everything holding the machine awake.");

    public static void Cleanup()
    {
        try
        {
            AppNotificationManager.Default.Unregister();
        }
        catch (Exception ex)
        {
            // Teardown, so nothing user-visible turns on it — but the detail is kept rather than
            // dropped on the floor.
            AppLog.Error("ToastService.Cleanup", ex);
        }
    }
}
