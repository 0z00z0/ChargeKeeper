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

    // DelayChips and LevelChips

    [Fact]
    public void DelayChips_TheSavedDelays_AreOfferedInOrder()
    {
        Assert.Equal(new[] { 10, 30, 60 }, LidDashboardPolicy.DelayChips([30, 10, 60], 10));
    }

    [Fact]
    public void DelayChips_TheDelayInUse_IsNotDuplicated()
    {
        // The boundary the Contains check exists for.
        Assert.Equal(new[] { 10, 30, 60 }, LidDashboardPolicy.DelayChips([10, 30, 60], 60));
    }

    [Fact]
    public void DelayChips_ADelayNoSavedOneCarries_IsFoldedInAtItsPlaceInTheOrder()
    {
        Assert.Equal(new[] { 10, 30, 45, 60 }, LidDashboardPolicy.DelayChips([10, 30, 60], 45));
        Assert.Equal(new[] { 2, 10, 30, 60 },  LidDashboardPolicy.DelayChips([10, 30, 60], 2));
    }

    [Fact]
    public void DelayChips_ADelayOutsideTheAllowedRange_IsClampedBeforeItReachesAChip()
    {
        // A chip writes its own value back, so an unreachable delay must never land on one.
        Assert.Equal(new[] { LidDelayPolicy.MinMinutes, 10, 30 }, LidDashboardPolicy.DelayChips([10, 30], 0));
        Assert.Equal(new[] { 10, 30, LidDelayPolicy.MaxMinutes }, LidDashboardPolicy.DelayChips([10, 30], 9_999));
    }

    [Fact]
    public void DelayChips_TwoSavedDelaysAtTheSameSpan_ShareOneChip()
    {
        Assert.Equal(new[] { 10, 30 }, LidDashboardPolicy.DelayChips([10, 30, 10], 10));
    }

    [Fact]
    public void LevelChips_TheSavedTargets_AreOfferedInOrderWithTheOneInUseFoldedIn()
    {
        Assert.Equal(new[] { 30, 40, 50, 70 }, LidDashboardPolicy.LevelChips([70, 50, 30], 40));
    }

    [Fact]
    public void LevelChips_ATargetOutsideTheAllowedRange_IsClampedBeforeItReachesAChip()
    {
        Assert.Equal(new[] { LidDischargeWatch.MinPercent, 30, 50 }, LidDashboardPolicy.LevelChips([30, 50], 0));
        Assert.Equal(new[] { 30, 50, LidDischargeWatch.MaxPercent }, LidDashboardPolicy.LevelChips([30, 50], 100));
    }

    // GroupsSideBySide — the boundary is six presets in total.

    [Fact]
    public void GroupsSideBySide_SixPresetsInTotal_StillFitBesideEachOther()
    {
        Assert.True(LidDashboardPolicy.GroupsSideBySide(delayCount: 3, levelCount: 3,
                                                       availableWidth: 300, minGroupWidth: 60));
    }

    [Fact]
    public void GroupsSideBySide_SevenPresetsInTotal_Stack()
    {
        // One past the boundary: the groups need a full width each rather than half of one.
        Assert.False(LidDashboardPolicy.GroupsSideBySide(delayCount: 4, levelCount: 3,
                                                        availableWidth: 300, minGroupWidth: 60));
    }

    [Fact]
    public void GroupsSideBySide_TheBoundaryIsTheTotal_NotEitherGroupOnItsOwn()
    {
        // Six in one group and none in the other is still six.
        Assert.True(LidDashboardPolicy.GroupsSideBySide(delayCount: 6, levelCount: 0,
                                                       availableWidth: 300, minGroupWidth: 60));
        Assert.False(LidDashboardPolicy.GroupsSideBySide(delayCount: 6, levelCount: 1,
                                                        availableWidth: 300, minGroupWidth: 60));
    }

    [Fact]
    public void GroupsSideBySide_TooNarrowForTwoGroups_StacksHoweverFewPresetsThereAre()
    {
        Assert.False(LidDashboardPolicy.GroupsSideBySide(delayCount: 1, levelCount: 1,
                                                        availableWidth: 100, minGroupWidth: 60));
    }

    [Fact]
    public void GroupsSideBySide_TheBoundaryIsNamedRatherThanSpeltTwice()
    {
        // The rule is "more than six stack", so the constant is what the layout must be read against.
        Assert.Equal(6, LidDashboardPolicy.MaxPresetsSideBySide);
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
    public void Describe_OnWithTheClockAlone_NamesTheDelay()
    {
        Assert.Equal("On — sleeps 10m after the lid closes",
            LidDashboardPolicy.Describe(enabled: true, timeEnabled: true, 10,
                                        dischargeEnabled: false, targetPercent: 50));
    }

    [Fact]
    public void Describe_Off_NamesWhatAppliesInstead()
    {
        // Off is not "nothing happens" — Windows handles the lid again, as the sections beside this
        // one also spell out.
        Assert.Equal("Off — the Windows lid setting applies",
            LidDashboardPolicy.Describe(enabled: false, timeEnabled: true, 10,
                                        dischargeEnabled: false, targetPercent: 50));
    }

    [Fact]
    public void Describe_ADelayOutsideTheAllowedRange_ReadsAsTheDelayThatWillActuallyRun()
    {
        Assert.Equal("On — sleeps 1m after the lid closes",
            LidDashboardPolicy.Describe(enabled: true, timeEnabled: true, 0,
                                        dischargeEnabled: false, targetPercent: 50));
    }

    [Fact]
    public void Describe_BothConditions_SaysWhicheverArrivesFirstDecides()
    {
        // The two are alternatives: a line reading as though both had to be satisfied would promise
        // a wait the machine no longer runs.
        Assert.Equal("On — sleeps 10m after the lid closes, or at 40 % battery, whichever comes first",
            LidDashboardPolicy.Describe(enabled: true, timeEnabled: true, 10,
                                        dischargeEnabled: true, targetPercent: 40));
    }

    [Fact]
    public void Describe_TheBatteryTargetAlone_NamesNoDelay()
    {
        Assert.Equal("On — sleeps at 40 % battery",
            LidDashboardPolicy.Describe(enabled: true, timeEnabled: false, 10,
                                        dischargeEnabled: true, targetPercent: 40));
    }

    [Fact]
    public void Describe_NeitherCondition_SaysTheMachineSleepsStraightAway()
    {
        // Nothing left to wait for, which is what the wait does rather than holding indefinitely.
        Assert.Equal("On — sleeps as soon as the lid closes",
            LidDashboardPolicy.Describe(enabled: true, timeEnabled: false, 10,
                                        dischargeEnabled: false, targetPercent: 40));
    }

    [Fact]
    public void Describe_ATargetOutsideTheAllowedRange_ReadsAsTheTargetThatWillActuallyApply()
    {
        Assert.Equal("On — sleeps at 95 % battery",
            LidDashboardPolicy.Describe(enabled: true, timeEnabled: false, 10,
                                        dischargeEnabled: true, targetPercent: 100));
    }

    [Fact]
    public void Describe_OffWithABatteryTarget_StillNamesTheWindowsSetting()
    {
        // With lid handling off the app has handed the lid back to Windows, target or no target.
        Assert.Equal("Off — the Windows lid setting applies",
            LidDashboardPolicy.Describe(enabled: false, timeEnabled: true, 10,
                                        dischargeEnabled: true, targetPercent: 40));
    }

    // ActiveChip and ActiveLevelChip

    [Fact]
    public void ActiveChip_LidHandlingOff_FillsNoChip()
    {
        // The chips stay on screen while the feature is off — they are the way to turn it on — but a
        // filled one would read as a delay that is running.
        Assert.Null(LidDashboardPolicy.ActiveChip(enabled: false, timeEnabled: true, 10));
    }

    [Fact]
    public void ActiveChip_TheClockConditionOff_FillsNoChip()
    {
        Assert.Null(LidDashboardPolicy.ActiveChip(enabled: true, timeEnabled: false, 10));
    }

    [Fact]
    public void ActiveChip_On_IsTheConfiguredDelay()
    {
        Assert.Equal(45, LidDashboardPolicy.ActiveChip(enabled: true, timeEnabled: true, 45));
    }

    [Fact]
    public void ActiveLevelChip_TheBatteryConditionOff_FillsNoChip()
    {
        Assert.Null(LidDashboardPolicy.ActiveLevelChip(enabled: true, dischargeEnabled: false, 40));
    }

    [Fact]
    public void ActiveLevelChip_On_IsTheConfiguredTarget()
    {
        Assert.Equal(40, LidDashboardPolicy.ActiveLevelChip(enabled: true, dischargeEnabled: true, 40));
    }

    [Fact]
    public void ActiveChip_IsAlwaysOneOfTheChipsOnOffer()
    {
        // Both are clamped the same way, so a hand-edited delay still highlights a real chip rather
        // than leaving the row looking off while the feature is on.
        int[] saved = [10, 30, 60];
        foreach (int minutes in new[] { 0, 1, 5, 7, 45, 60, 120, 240, 9_999 })
        {
            int? active = LidDashboardPolicy.ActiveChip(enabled: true, timeEnabled: true, minutes);
            Assert.Contains(active!.Value, LidDashboardPolicy.DelayChips(saved, minutes));
        }
    }

    [Fact]
    public void ActiveLevelChip_IsAlwaysOneOfTheChipsOnOffer()
    {
        int[] saved = [30, 50, 70];
        foreach (int percent in new[] { 0, 5, 17, 50, 95, 100, 9_999 })
        {
            int? active = LidDashboardPolicy.ActiveLevelChip(enabled: true, dischargeEnabled: true, percent);
            Assert.Contains(active!.Value, LidDashboardPolicy.LevelChips(saved, percent));
        }
    }
}
