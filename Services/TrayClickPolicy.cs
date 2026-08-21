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
/// PURE decision for a tray left-click — single click toggles the dashboard, a second click inside
/// the system double-click window opens Settings instead. No clock, no window: the caller passes the
/// timestamps, so the ordering rules are unit-testable without a tray icon or a real double-click.
/// House style; see <see cref="ThresholdCapabilityPolicy"/>.
/// <para>
/// The double-click test comes FIRST, ahead of both the visible-toggle and the reopen-guard branches,
/// and that ordering is the whole point. The dashboard is a topmost popup that auto-hides when it
/// loses focus, so by the time the second click's command runs the window has usually hidden itself
/// already — leaving the state "not visible, hidden a few ms ago", which is exactly what the reopen
/// guard exists to swallow. Testing the click interval first means the guard can no longer eat a
/// genuine double-click, while a slower second click still falls through to the unchanged toggle +
/// guard behaviour.
/// </para>
/// </summary>
internal static class TrayClickPolicy
{
    /// <summary>
    /// Decides what this click does.
    /// <para><paramref name="doubleClickInterval"/> is the SYSTEM setting (<c>GetDoubleClickTime</c>),
    /// which the user can change in the mouse control panel — never a hardcoded 500 ms.</para>
    /// <para><paramref name="sinceHidden"/>/<paramref name="reopenGuard"/> are the existing guard: a
    /// click that lands while the popup is open first deactivates it, and re-showing it from that same
    /// click would make the dashboard flash rather than close.</para>
    /// </summary>
    /// <param name="previousClickAt">When the previous left-click arrived, or null if there was none
    /// (or the last one already resolved to a double-click, which ends the pair).</param>
    public static TrayClickAction Decide(
        DateTimeOffset now,
        DateTimeOffset? previousClickAt,
        TimeSpan doubleClickInterval,
        bool dashboardVisible,
        TimeSpan sinceHidden,
        TimeSpan reopenGuard)
    {
        if (previousClickAt is { } previous && now - previous <= doubleClickInterval)
            return TrayClickAction.OpenSettingsAndHideDashboard;

        if (dashboardVisible) return TrayClickAction.HideDashboard;

        return sinceHidden > reopenGuard ? TrayClickAction.OpenDashboard : TrayClickAction.None;
    }
}
