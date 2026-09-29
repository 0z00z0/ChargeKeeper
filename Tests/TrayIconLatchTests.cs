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
}
