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
        try
        {
            var builder = new AppNotificationBuilder()
                .AddText(title)
                .AddText(body);

            AppNotificationManager.Default.Show(builder.BuildNotification());
            AppLog.Info(NotificationMessages.Shown(kind, atPercent));
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
