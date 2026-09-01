using ChargeKeeper.Helpers;
using ChargeKeeper.Services;
using ChargeKeeper.Vendors;
using Xunit;

namespace ChargeKeeper.Tests;

// The dedupe latch behind the tray icon. Every failure it guards is silent on screen: the icon
// simply keeps showing whatever it showed before, and on AC held at a stop threshold the reading
// that would trigger the next repaint does not change for hours.
public class TrayIconLatchTests
{
    private static TrayIconRequest Arc(int pct, PowerState state = PowerState.Discharging,
                                       ChargeThresholdState? threshold = null) =>
        new(pct, state, TrayIconMode.Arc, threshold);

    [Fact]
    public void BeforeAnythingIsPainted_EveryRequestNeedsARepaint()
    {
        var latch = new TrayIconLatch();
        Assert.True(latch.NeedsRepaint(Arc(0)));
        Assert.True(latch.NeedsRepaint(Arc(80, PowerState.Charging)));
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
    public void EveryPowerStateEdgeAtTheSamePercentage_Repaints()
    {
        // Charging and idle-on-mains are painted from different scales, and the edge between them
        // moves no other input: a dedupe key carrying only an on-AC flag leaves the wrong colour on
        // screen with nothing to say so.
        foreach (var (painted, next) in new[]
        {
            (PowerState.Discharging, PowerState.Charging),
            (PowerState.Charging,    PowerState.IdleOnMains),
            (PowerState.IdleOnMains, PowerState.Discharging),
            (PowerState.IdleOnMains, PowerState.Charging),
        })
        {
            var latch = new TrayIconLatch();
            latch.MarkPainted(Arc(80, painted));
            Assert.True(latch.NeedsRepaint(Arc(80, next)),
                        $"{painted} → {next} at 80 % did not repaint.");
        }
    }

    [Fact]
    public void AStyleChangeAloneRepaints_EvenWhenTheReadingHasNotMoved()
    {
        var latch = new TrayIconLatch();
        latch.MarkPainted(new TrayIconRequest(80, PowerState.Discharging, TrayIconMode.Arc, null));
        Assert.True(latch.NeedsRepaint(new TrayIconRequest(80, PowerState.Discharging, TrayIconMode.Numeric, null)));
    }

    [Fact]
    public void AThresholdChangeAloneRepaints_BecauseTheIconCarriesTheMarks()
    {
        var latch = new TrayIconLatch();
        latch.MarkPainted(Arc(80, threshold: new ChargeThresholdState(true, true, 60, 80)));
        Assert.True(latch.NeedsRepaint(Arc(80, threshold: new ChargeThresholdState(true, true, 70, 90))));
        // An equal-by-value state is the same picture, though.
        latch.MarkPainted(Arc(80, threshold: new ChargeThresholdState(true, true, 70, 90)));
        Assert.False(latch.NeedsRepaint(Arc(80, threshold: new ChargeThresholdState(true, true, 70, 90))));
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
        Assert.Equal((0, PowerState.Discharging),
                     TrayIconLatch.ReadingOrUnknown((-1, PowerState.Discharging)));
    }

    [Fact]
    public void AfterTheFirstBatteryReport_AForcedRepaintDrawsThatReading()
    {
        Assert.Equal((80, PowerState.Charging), TrayIconLatch.ReadingOrUnknown((80, PowerState.Charging)));
        // 0 % is a real reading.
        Assert.Equal((0, PowerState.IdleOnMains), TrayIconLatch.ReadingOrUnknown((0, PowerState.IdleOnMains)));
    }
}
