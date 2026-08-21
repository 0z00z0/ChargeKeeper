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

    // Every notification is fire-and-forget and must never crash the app, so the build+show+swallow
    // scaffold lives here once.
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

    /// <summary><paramref name="dropPercent"/> is always positive — the caller filters rises and flats.</summary>
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
