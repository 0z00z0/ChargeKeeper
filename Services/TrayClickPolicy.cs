namespace ChargeKeeper.Services;

/// <summary>What a left-click on the tray icon should do.</summary>
internal enum TrayClickAction
{
    /// <summary>Swallow the click — see <see cref="TrayClickPolicy.Decide"/>'s reopen guard.</summary>
    None,
    /// <summary>Show the dashboard near the tray.</summary>
    OpenDashboard,
    /// <summary>Hide the dashboard.</summary>
    HideDashboard,
    /// <summary>Open the Settings window and hide the dashboard behind it.</summary>
    OpenSettingsAndHideDashboard,
}

/// <summary>
/// Pure decision for a tray left-click: one click toggles the dashboard, a second inside the system
/// double-click window opens Settings. The caller passes the timestamps, so this stays testable.
/// </summary>
internal static class TrayClickPolicy
{
    /// <summary>
    /// <paramref name="doubleClickInterval"/> is the system setting (<c>GetDoubleClickTime</c>);
    /// <paramref name="sinceHidden"/>/<paramref name="reopenGuard"/> swallow the click that just
    /// dismissed the popup by taking focus off it.
    /// </summary>
    /// <param name="previousClickAt">Null when there was none, or when the last one already resolved
    /// to a double-click, which ends the pair.</param>
    public static TrayClickAction Decide(
        DateTimeOffset now,
        DateTimeOffset? previousClickAt,
        TimeSpan doubleClickInterval,
        bool dashboardVisible,
        TimeSpan sinceHidden,
        TimeSpan reopenGuard)
    {
        // Tested before the reopen guard: the popup auto-hides on losing focus, so by the time the
        // second click runs the guard would otherwise swallow it as a reopen.
        if (previousClickAt is { } previous && now - previous <= doubleClickInterval)
            return TrayClickAction.OpenSettingsAndHideDashboard;

        if (dashboardVisible) return TrayClickAction.HideDashboard;

        return sinceHidden > reopenGuard ? TrayClickAction.OpenDashboard : TrayClickAction.None;
    }
}
