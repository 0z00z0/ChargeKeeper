using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

// The pure decisions behind the dashboard's Lid close section — no power scheme, no window.
public class LidDashboardPolicyTests
{
    // ShouldShow

    [Fact]
    public void ShouldShow_NoLid_HidesTheSection()
    {
        // A desktop has nothing to delay, and a section claiming otherwise reads as a detection bug.
        Assert.False(LidDashboardPolicy.ShouldShow(lidPresent: false, enabled: false, hasSavedLidAction: false));
    }

    [Fact]
    public void ShouldShow_LidPresent_ShowsIt()
    {
        Assert.True(LidDashboardPolicy.ShouldShow(lidPresent: true, enabled: false, hasSavedLidAction: false));
    }

    [Fact]
    public void ShouldShow_NoLidButEnabled_StillShowsIt()
    {
        // settings.json roams: the feature can arrive on already, and the switch that turns it back
        // off must not be the one thing the machine hides.
        Assert.True(LidDashboardPolicy.ShouldShow(lidPresent: false, enabled: true, hasSavedLidAction: false));
    }

    [Fact]
    public void ShouldShow_NoLidButALidActionIsStillSaved_StillShowsIt()
    {
        // A saved action means the Windows lid-close setting is parked on this app's override.
        Assert.True(LidDashboardPolicy.ShouldShow(lidPresent: false, enabled: false, hasSavedLidAction: true));
    }

    // Chips

    [Fact]
    public void Chips_ADelayFromTheQuickList_OffersJustThatList()
    {
        Assert.Equal(new[] { 5, 10, 30, 60 }, LidDashboardPolicy.Chips(10));
    }

    [Fact]
    public void Chips_TheLastQuickValue_IsNotDuplicated()
    {
        // The boundary the Contains check exists for.
        Assert.Equal(new[] { 5, 10, 30, 60 }, LidDashboardPolicy.Chips(60));
    }

    [Fact]
    public void Chips_ADelaySetInSettings_IsFoldedInAtItsPlaceInTheOrder()
    {
        Assert.Equal(new[] { 5, 10, 30, 45, 60 }, LidDashboardPolicy.Chips(45));
        Assert.Equal(new[] { 2, 5, 10, 30, 60 },  LidDashboardPolicy.Chips(2));
        Assert.Equal(new[] { 5, 10, 30, 60, 120 }, LidDashboardPolicy.Chips(120));
    }

    [Fact]
    public void Chips_ADelayOutsideTheAllowedRange_IsClampedBeforeItReachesAChip()
    {
        // A chip writes its own value back, so an unreachable delay must never land on one.
        Assert.Equal(new[] { LidDelayPolicy.MinMinutes, 5, 10, 30, 60 }, LidDashboardPolicy.Chips(0));
        Assert.Equal(new[] { 5, 10, 30, 60, LidDelayPolicy.MaxMinutes }, LidDashboardPolicy.Chips(9_999));
    }

    // ShortLabel

    [Theory]
    [InlineData(1, "1m")]
    [InlineData(45, "45m")]
    [InlineData(59, "59m")]
    [InlineData(60, "1h")]
    [InlineData(90, "1h30")]
    [InlineData(120, "2h")]
    [InlineData(240, "4h")]
    public void ShortLabel_IsChipSized(int minutes, string expected)
    {
        Assert.Equal(expected, LidDashboardPolicy.ShortLabel(minutes));
    }

    // Describe

    [Fact]
    public void Describe_On_NamesTheDelay()
    {
        Assert.Equal("On — sleeps 10m after the lid closes", LidDashboardPolicy.Describe(enabled: true, 10));
    }

    [Fact]
    public void Describe_Off_NamesWhatAppliesInstead()
    {
        // Off is not "nothing happens" — Windows handles the lid again, as the sections beside this
        // one also spell out.
        Assert.Equal("Off — the Windows lid setting applies", LidDashboardPolicy.Describe(enabled: false, 10));
    }

    [Fact]
    public void Describe_ADelayOutsideTheAllowedRange_ReadsAsTheDelayThatWillActuallyRun()
    {
        Assert.Equal("On — sleeps 1m after the lid closes", LidDashboardPolicy.Describe(enabled: true, 0));
    }

    // ActiveChip

    [Fact]
    public void ActiveChip_Off_FillsNoChip()
    {
        // The chips stay on screen while the feature is off — they are the way to turn it on — but a
        // filled one would read as a delay that is running.
        Assert.Null(LidDashboardPolicy.ActiveChip(enabled: false, 10));
    }

    [Fact]
    public void ActiveChip_On_IsTheConfiguredDelay()
    {
        Assert.Equal(45, LidDashboardPolicy.ActiveChip(enabled: true, 45));
    }

    [Fact]
    public void ActiveChip_IsAlwaysOneOfTheChipsOnOffer()
    {
        // The two are clamped the same way, so a hand-edited delay still highlights a real chip
        // rather than leaving the row looking off while the feature is on.
        foreach (int minutes in new[] { 0, 1, 5, 7, 45, 60, 120, 240, 9_999 })
        {
            int? active = LidDashboardPolicy.ActiveChip(enabled: true, minutes);
            Assert.Contains(active!.Value, LidDashboardPolicy.Chips(minutes));
        }
    }
}
