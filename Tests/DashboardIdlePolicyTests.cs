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
        // The hidden window is retained so opening the popup every couple of minutes costs no rebuild.
        Assert.False(DashboardIdlePolicy.ShouldClose(isVisible: false, sinceHidden: TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void ShouldClose_VisibleWindow_False_EvenWhenLongIdle()
    {
        // A stale tick: the idle timer fired and queued, then a tray click re-showed the window
        // before delivery. SinceHidden is only stamped on hide, so it still reads long, and
        // visibility is all that stops the popup closing the instant it opens.
        Assert.False(DashboardIdlePolicy.ShouldClose(isVisible: true, sinceHidden: TimeSpan.FromHours(2)));
    }

    [Fact]
    public void ShouldClose_ExactlyAtIdlePeriod_True()
    {
        // A DispatcherTimer tick lands at, or a hair past, its interval, so a strictly-greater
        // comparison would skip the close the timer was armed for.
        Assert.True(DashboardIdlePolicy.ShouldClose(isVisible: false, sinceHidden: DashboardIdlePolicy.IdleCloseAfter));
    }
}
