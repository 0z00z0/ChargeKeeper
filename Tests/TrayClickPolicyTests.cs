using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The tray left-click gesture. Every case here is one a real double-click cannot be trusted to
/// reproduce by hand: the interval is a user setting, and the dashboard hides ITSELF between the two
/// clicks, so what the second click actually sees is "not visible, hidden 2 ms ago" — the exact state
/// the reopen guard exists to swallow.
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
        // No previous click at all — the ordinary case, and the one that must stay instant.
        Assert.Equal(
            TrayClickAction.OpenDashboard,
            Decide(T0, previous: null, visible: false, sinceHidden: LongHidden));
    }

    [Fact]
    public void SecondClickInsideTheWindow_OpensSettingsAndHides()
    {
        // The user's gesture: two quick clicks → Settings, dashboard out of the way.
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
        // Slow second click on a VISIBLE dashboard: the pre-existing hide-on-second-click behaviour,
        // unchanged.
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
        // The guard's own case, preserved: the click that dismissed the popup must not re-show it.
        // Reached only because this click is outside the double-click window.
        Assert.Equal(
            TrayClickAction.None,
            Decide(T0 + TimeSpan.FromSeconds(3), previous: T0,
                   visible: false, sinceHidden: TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public void DoubleClickBeatsTheReopenGuard()
    {
        // The interaction that makes the whole feature work. The second click of a double-click
        // arrives on a dashboard that hid ITSELF on losing focus, i.e. inside the reopen guard — so if
        // the guard were tested first this gesture would be swallowed and Settings would never open.
        Assert.Equal(
            TrayClickAction.OpenSettingsAndHideDashboard,
            Decide(T0 + TimeSpan.FromMilliseconds(180), previous: T0,
                   visible: false, sinceHidden: TimeSpan.FromMilliseconds(20)));
    }

    [Fact]
    public void PairEndsAfterADouble_SoAThirdRapidClickOpensTheDashboard()
    {
        // App clears the timestamp once a double resolves. Without that, a third rapid click would
        // pair with the second and open Settings again on a machine the user is just clicking at.
        Assert.Equal(
            TrayClickAction.OpenDashboard,
            Decide(T0 + TimeSpan.FromMilliseconds(360), previous: null,
                   visible: false, sinceHidden: LongHidden));
    }

    [Fact]
    public void SystemIntervalIsHonoured_NotAHardcoded500Ms()
    {
        // A user who slowed the double-click speed down gets the gesture at THEIR interval. The same
        // 700 ms gap is a double at 1000 ms and a plain toggle at 500 ms.
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
