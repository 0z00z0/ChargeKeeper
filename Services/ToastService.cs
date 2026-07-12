using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace ChargeKeeper.Services;

internal static class ToastService
{
    private static bool _registered;

    public static void Register()
    {
        if (_registered)
            return;

        try
        {
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch
        {
            // Toast registration failure must not crash the app.
        }
    }

    // The one shared show path: every notification is fire-and-forget and must never crash the
    // app, so the build+show+swallow scaffold lives here once instead of being copied per toast.
    private static void TryShow(string title, string body)
    {
        try
        {
            var builder = new AppNotificationBuilder()
                .AddText(title)
                .AddText(body);

            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch
        {
            // Toast failure must not crash the app.
        }
    }

    public static void NotifyChargeComplete(int stopPct) =>
        TryShow("Battery charged", stopPct == 100
            ? "Fully charged"
            : $"Smart Charge stopped at {stopPct}%  —  charged to limit");

    public static void NotifyChargingStarted() =>
        TryShow("Charging", "AC power connected");

    public static void NotifyLowBattery(int pct) =>
        TryShow("Low battery", $"Battery at {pct}% — connect AC power");

    /// <summary>
    /// Overnight-drain anomaly (TODO #26): the battery lost more charge than expected across a
    /// downtime gap. <paramref name="dropPercent"/> is always positive here (the caller filters out
    /// a rise/flat reading before calling this) and <paramref name="duration"/> is the gap's real
    /// elapsed span, formatted coarsely (hours, or minutes under an hour) since a precise duration
    /// isn't the point of this toast — the drop and a nudge toward Modern Standby are.
    /// </summary>
    public static void NotifyDrainAnomaly(int dropPercent, TimeSpan duration)
    {
        string span = duration.TotalHours >= 1 ? $"{duration.TotalHours:0.#}h" : $"{duration.Minutes}m";
        TryShow("Unusual battery drain", $"Lost {dropPercent}% over {span} while asleep — Modern Standby misbehaving?");
    }

    public static void Cleanup()
    {
        try
        {
            AppNotificationManager.Default.Unregister();
        }
        catch
        {
            // Cleanup failure must not crash the app.
        }
    }
}
