using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The tray left-click gesture. The interval is a user setting, and the dashboard hides itself
/// between the two clicks, so the second click sees "not visible, hidden 2 ms ago" — the exact
/// state the reopen guard exists to swallow.
/// </summary>
public class TrayClickPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 21, 14, 3, 11, TimeSpan.Zero);

    private static readonly TimeSpan DoubleClick = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ReopenGuard = TimeSpan.FromMilliseconds(300);

    /// <summary>Long enough that neither the double-click window nor the reopen guard is in play.</summary>
    private static readonly TimeSpan LongHidden = TimeSpan.FromMinutes(1);

    private static TrayClickAction Decide(
        DateTimeOffset now, DateTimeOffset? previous, bool visible, TimeSpan sinceHidden) =>
        TrayClickPolicy.Decide(now, previous, DoubleClick, visible, sinceHidden, ReopenGuard);

    [Fact]
    public void FirstClick_OpensTheDashboard()
    {
        // No previous click: the ordinary case, and the one that must stay instant.
        Assert.Equal(
            TrayClickAction.OpenDashboard,
            Decide(T0, previous: null, visible: false, sinceHidden: LongHidden));
    }

    [Fact]
    public void SecondClickInsideTheWindow_OpensSettingsAndHides()
    {
        // Two quick clicks open Settings and put the dashboard out of the way.
        Assert.Equal(
            TrayClickAction.OpenSettingsAndHideDashboard,
            Decide(T0 + TimeSpan.FromMilliseconds(200), previous: T0, visible: true, sinceHidden: TimeSpan.Zero));
    }

    [Fact]
    public void SecondClickExactlyAtTheInterval_StillCountsAsADouble()
    {
        // GetDoubleClickTime is the inclusive bound Windows itself uses; a click landing exactly on it
        // must not fall out of the gesture.
        Assert.Equal(
            TrayClickAction.OpenSettingsAndHideDashboard,
            Decide(T0 + DoubleClick, previous: T0, visible: true, sinceHidden: TimeSpan.Zero));
    }

    [Fact]
    public void SecondClickOutsideTheWindow_IsAPlainToggle()
    {
        // Slow second click on a visible dashboard hides it.
        Assert.Equal(
            TrayClickAction.HideDashboard,
            Decide(T0 + TimeSpan.FromSeconds(3), previous: T0, visible: true, sinceHidden: TimeSpan.Zero));
    }

    [Fact]
    public void SecondClickOutsideTheWindow_ReopensWhenTheDashboardIsLongHidden()
    {
        // Slow second click with the dashboard already away: a fresh open, not a swallowed click.
        Assert.Equal(
            TrayClickAction.OpenDashboard,
            Decide(T0 + TimeSpan.FromSeconds(3), previous: T0, visible: false, sinceHidden: LongHidden));
    }

    [Fact]
    public void ClickThatJustAutoHidTheDashboard_IsSwallowedByTheReopenGuard()
    {
        // The guard's own case: the click that dismissed the popup must not re-show it. Reached only
        // because this click falls outside the double-click window.
        Assert.Equal(
            TrayClickAction.None,
            Decide(T0 + TimeSpan.FromSeconds(3), previous: T0,
                   visible: false, sinceHidden: TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public void DoubleClickBeatsTheReopenGuard()
    {
        // The second click of a double arrives on a dashboard that hid itself on losing focus, so it
        // lands inside the reopen guard. Testing the guard first would swallow the gesture.
        Assert.Equal(
            TrayClickAction.OpenSettingsAndHideDashboard,
            Decide(T0 + TimeSpan.FromMilliseconds(180), previous: T0,
                   visible: false, sinceHidden: TimeSpan.FromMilliseconds(20)));
    }

    [Fact]
    public void PairEndsAfterADouble_SoAThirdRapidClickOpensTheDashboard()
    {
        // App clears the timestamp once a double resolves; otherwise a third rapid click would pair
        // with the second and open Settings again.
        Assert.Equal(
            TrayClickAction.OpenDashboard,
            Decide(T0 + TimeSpan.FromMilliseconds(360), previous: null,
                   visible: false, sinceHidden: LongHidden));
    }

    [Fact]
    public void SystemIntervalIsHonoured_NotAHardcoded500Ms()
    {
        // The gesture follows the user's own double-click speed: the same 700 ms gap is a double at
        // 1000 ms and a plain toggle at 500 ms.
        var slow = TimeSpan.FromMilliseconds(1000);
        var gap  = T0 + TimeSpan.FromMilliseconds(700);

        Assert.Equal(
            TrayClickAction.OpenSettingsAndHideDashboard,
            TrayClickPolicy.Decide(gap, T0, slow, dashboardVisible: true,
                                   sinceHidden: TimeSpan.Zero, reopenGuard: ReopenGuard));

        Assert.Equal(
            TrayClickAction.HideDashboard,
            Decide(gap, previous: T0, visible: true, sinceHidden: TimeSpan.Zero));
    }
}
