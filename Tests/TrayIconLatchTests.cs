using ChargeKeeper.Helpers;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

// The dedupe latch behind the tray icon. Every failure it guards is silent on screen: the icon
// simply keeps showing whatever it showed before, and on AC held at a stop threshold the reading
// that would trigger the next repaint does not change for hours.
public class TrayIconLatchTests
{
    private static TrayIconRequest Arc(int pct, bool charging = false) =>
        new(pct, charging, TrayIconMode.Arc);

    [Fact]
    public void BeforeAnythingIsPainted_EveryRequestNeedsARepaint()
    {
        var latch = new TrayIconLatch();
        Assert.True(latch.NeedsRepaint(Arc(0)));
        Assert.True(latch.NeedsRepaint(Arc(80, charging: true)));
    }

    [Fact]
    public void ARepaintThatLanded_IsNotRepeated()
    {
        var latch = new TrayIconLatch();
        latch.MarkPainted(Arc(80));
        Assert.False(latch.NeedsRepaint(Arc(80)));
    }

    [Fact]
    public void ARepaintThatNeverLanded_IsRetriedOnTheNextTick()
    {
        // The #110 shape: the request was made, the render was refused or threw, so nothing marked
        // it painted. The next tick carrying the same reading must still repaint.
        var latch = new TrayIconLatch();
        Assert.True(latch.NeedsRepaint(Arc(80)));
        Assert.True(latch.NeedsRepaint(Arc(80)));
    }

    [Fact]
    public void ChargingEdgeAtTheSamePercentage_Repaints()
    {
        var latch = new TrayIconLatch();
        latch.MarkPainted(Arc(80, charging: false));
        Assert.True(latch.NeedsRepaint(Arc(80, charging: true)));
    }

    [Fact]
    public void AStyleChangeAloneRepaints_EvenWhenTheReadingHasNotMoved()
    {
        var latch = new TrayIconLatch();
        latch.MarkPainted(new TrayIconRequest(80, false, TrayIconMode.Arc));
        Assert.True(latch.NeedsRepaint(new TrayIconRequest(80, false, TrayIconMode.Numeric)));
    }

    [Fact]
    public void Invalidate_ForcesTheNextRepaint()
    {
        // The slot size and the tray icon's own recreation change the pixels without changing the
        // request, so the latch has to be droppable.
        var latch = new TrayIconLatch();
        latch.MarkPainted(Arc(80));
        latch.Invalidate();
        Assert.True(latch.NeedsRepaint(Arc(80)));
    }

    [Fact]
    public void BeforeTheFirstBatteryReport_AForcedRepaintDrawsTheUnknownState()
    {
        // -1 is the "not yet read" seed. Returning it unchanged is what made a style change in
        // Settings do visibly nothing until the first tick arrived.
        Assert.Equal((0, false), TrayIconLatch.ReadingOrUnknown((-1, false)));
    }

    [Fact]
    public void AfterTheFirstBatteryReport_AForcedRepaintDrawsThatReading()
    {
        Assert.Equal((80, true), TrayIconLatch.ReadingOrUnknown((80, true)));
        Assert.Equal((0, true),  TrayIconLatch.ReadingOrUnknown((0, true)));   // 0 % is a real reading
    }
}
