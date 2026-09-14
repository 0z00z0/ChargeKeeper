using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The "a reading has been approved at least once this run" state issue #205 adds. Exercised in
/// isolation from the gate and the hardware read, both of which feed it one outcome at a time.
/// </summary>
public class ThermalApprovalLatchTests
{
    [Fact]
    public void BeforeAnyApproval_TheLatchIsFalse()
    {
        var latch = new ThermalApprovalLatch();
        Assert.False(latch.HasEverApproved);
    }

    [Fact]
    public void AnApprovedOutcome_LatchesTrueAndReportsItWasFirst()
    {
        var latch = new ThermalApprovalLatch();
        Assert.True(latch.Observe(approved: true));
        Assert.True(latch.HasEverApproved);
    }

    [Fact]
    public void AWithheldOutcomeAfterApproval_LeavesTheLatchTrue()
    {
        // The gate withholds a currently-constant reading long after it has already proved itself —
        // stretches of several minutes, under steady load. The latch must not read that as "back to
        // no trustworthy reading".
        var latch = new ThermalApprovalLatch();
        latch.Observe(approved: true);

        bool secondCallReportedFirst = latch.Observe(approved: false);

        Assert.False(secondCallReportedFirst);
        Assert.True(latch.HasEverApproved);
    }

    [Fact]
    public void OnlyTheFirstApprovalReportsItWasFirst()
    {
        // The caller raises a one-shot event on a true return; a second true return would raise it
        // twice for a feature documented as firing once.
        var latch = new ThermalApprovalLatch();
        Assert.True(latch.Observe(approved: true));
        Assert.False(latch.Observe(approved: true));
    }

    [Fact]
    public void WithheldOutcomesBeforeAnyApproval_NeverLatch()
    {
        var latch = new ThermalApprovalLatch();
        Assert.False(latch.Observe(approved: false));
        Assert.False(latch.Observe(approved: false));
        Assert.False(latch.HasEverApproved);
    }
}
