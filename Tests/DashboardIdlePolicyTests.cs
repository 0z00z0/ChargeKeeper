using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

public class DashboardIdlePolicyTests
{
    [Fact]
    public void ShouldClose_HiddenLongerThanIdlePeriod_True()
    {
        Assert.True(DashboardIdlePolicy.ShouldClose(isVisible: false, sinceHidden: TimeSpan.FromHours(2)));
    }

    [Fact]
    public void ShouldClose_HiddenBriefly_False_PopupStaysCheapToReopen()
    {
        // The whole point of retaining the hidden window: a user who flicks the popup open every
        // couple of minutes must not pay a rebuild each time.
        Assert.False(DashboardIdlePolicy.ShouldClose(isVisible: false, sinceHidden: TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void ShouldClose_VisibleWindow_False_EvenWhenLongIdle()
    {
        // The stale-tick case this guard exists for: the idle timer fired and was queued on the
        // dispatcher, then the user's tray click re-showed the window before the tick was delivered.
        // SinceHidden still reads long (it is only stamped on hide), so visibility is the ONLY thing
        // stopping the popup from closing the instant the user opened it.
        Assert.False(DashboardIdlePolicy.ShouldClose(isVisible: true, sinceHidden: TimeSpan.FromHours(2)));
    }

    [Fact]
    public void ShouldClose_ExactlyAtIdlePeriod_True()
    {
        // Boundary: a DispatcherTimer tick can be delivered at (or a hair past) its interval, so a
        // strictly-greater comparison here would skip the very close this timer was armed for and
        // leave the window retained until some later hide/show cycle.
        Assert.True(DashboardIdlePolicy.ShouldClose(isVisible: false, sinceHidden: DashboardIdlePolicy.IdleCloseAfter));
    }
}
