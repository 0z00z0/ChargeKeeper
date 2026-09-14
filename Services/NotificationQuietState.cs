using System.Runtime.InteropServices;
using Windows.UI.Shell;

namespace ChargeKeeper.Services;

/// <summary>What Windows reported about holding notifications back, at one moment.</summary>
/// <param name="UserNotificationState">The QUERY_USER_NOTIFICATION_STATE value, or null when the
/// query failed.</param>
/// <param name="FocusSessionActive">Whether a Windows focus session is running, which switches Do
/// not disturb on.</param>
internal readonly record struct NotificationQuietReading(int? UserNotificationState, bool FocusSessionActive)
{
    /// <summary>QUNS_ACCEPTS_NOTIFICATIONS. Every other value — locked, full screen, presentation
    /// mode, quiet time — is one where Windows holds notifications back.</summary>
    public const int AcceptsNotifications = 5;

    /// <summary>Whether the application's own sound may play beside a notification. A silenced
    /// notification relies on this for the quiet Windows would otherwise give it. A failed query
    /// does not hold the sound back: detection is unavailable, and a chosen sound that never plays
    /// is the harder fault to notice.</summary>
    public bool AllowsSound =>
        !FocusSessionActive && (UserNotificationState is null || UserNotificationState == AcceptsNotifications);
}

/// <summary>Reads whether Windows is holding notifications back. No documented interface reports a
/// Do not disturb switched on by hand outside a focus session, so that case is not detected.</summary>
internal static class NotificationQuietState
{
    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    public static NotificationQuietReading Read() => new(UserNotificationState(), FocusSessionActive());

    private static int? UserNotificationState()
    {
        try
        {
            return SHQueryUserNotificationState(out int state) == 0 ? state : null;
        }
        catch (Exception ex)
        {
            AppLog.Error("NotificationQuietState.UserNotificationState", ex);
            return null;
        }
    }

    private static bool FocusSessionActive()
    {
        try
        {
            // Absent before Windows 11 22H2, where the class cannot be activated at all.
            return FocusSessionManager.IsSupported && FocusSessionManager.GetDefault().IsFocusActive;
        }
        catch (Exception ex)
        {
            AppLog.Error("NotificationQuietState.FocusSessionActive", ex);
            return false;
        }
    }
}
