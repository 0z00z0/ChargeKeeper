using System.Linq;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// What is holding the machine awake, read from the text <c>powercfg /requests</c> prints. The
/// application takes holds of its own for Keep Awake and for a lid-close wait, so the case that
/// matters most is that its own hold is named as its own rather than sent somebody hunting for a
/// stranger.
/// </summary>
/// <remarks>The samples below use the tokens measured out of the command's own resources: the
/// category headings, the empty-category line and the three bracketed caller types.</remarks>
public class PowerRequestTextTests
{
    private const string ThisApp = "ChargeKeeper.exe";

    private const string Sample = """
        DISPLAY:
        None.

        SYSTEM:
        [DRIVER] Audio Device on High Definition Audio Bus
        An audio stream is currently in use.
        [PROCESS] \Device\HarddiskVolume3\Users\someone\AppData\Local\Programs\ChargeKeeper\ChargeKeeper.exe

        AWAYMODE:
        None.

        EXECUTION:
        [PROCESS] \Device\HarddiskVolume3\Program Files\Something\player.exe
        Playing a video.

        PERFBOOST:
        None.

        ACTIVELOCKSCREEN:
        None.
        """;

    [Fact]
    public void AHoldThisApplicationTook_IsNamedAsItsOwn()
    {
        var holds = PowerRequestText.Parse(Sample, ThisApp);

        Assert.NotNull(holds);
        var mine = Assert.Single(holds!.Where(h => h.IsThisApplication));
        Assert.Equal("SYSTEM", mine.Category);
        Assert.Equal("PROCESS", mine.Kind);
        Assert.Equal(ThisApp, mine.ShortHolder);
    }

    [Fact]
    public void EveryOtherHold_IsNotClaimedAsThisApplicationsOwn()
    {
        var holds = PowerRequestText.Parse(Sample, ThisApp);

        Assert.NotNull(holds);
        Assert.All(holds!.Where(h => h.ShortHolder != ThisApp), h => Assert.False(h.IsThisApplication));
    }

    [Fact]
    public void TheHolderIsMatchedOnTheFileNameAlone()
    {
        // Windows names a process here by a device path; the same file is named by a drive letter
        // everywhere else, and the two cannot be compared as written.
        Assert.True(PowerRequestText.IsThisApplication(
            @"\Device\HarddiskVolume3\Users\someone\ChargeKeeper\ChargeKeeper.exe", ThisApp));
        Assert.True(PowerRequestText.IsThisApplication(@"C:\Programs\ChargeKeeper\chargekeeper.EXE", ThisApp));
        Assert.False(PowerRequestText.IsThisApplication(@"C:\Programs\Other\ChargeKeeperHelper.exe", ThisApp));
        Assert.False(PowerRequestText.IsThisApplication(@"C:\Programs\ChargeKeeper\ChargeKeeper.exe", ""));
    }

    [Fact]
    public void EachHoldKeepsItsCategoryKindAndStatedReason()
    {
        var holds = PowerRequestText.Parse(Sample, ThisApp);

        Assert.NotNull(holds);
        var driver = Assert.Single(holds!.Where(h => h.Kind == "DRIVER"));
        Assert.Equal("SYSTEM", driver.Category);
        Assert.Equal("An audio stream is currently in use.", driver.Reason);

        var player = Assert.Single(holds.Where(h => h.ShortHolder == "player.exe"));
        Assert.Equal("EXECUTION", player.Category);
        Assert.Equal("Playing a video.", player.Reason);
    }

    [Fact]
    public void AnEmptyCategoryContributesNothing()
    {
        var holds = PowerRequestText.Parse(Sample, ThisApp);

        Assert.NotNull(holds);
        Assert.DoesNotContain(holds!, h => h.Category is "DISPLAY" or "AWAYMODE" or "PERFBOOST");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("This command requires administrator privileges and must be executed from an elevated command prompt.")]
    public void TextTheParserDoesNotUnderstand_ReadsAsNoReadingRatherThanAnEmptyOne(string output)
    {
        // "Nothing is holding the machine awake" is a claim; a refusal is no evidence for it.
        Assert.Null(PowerRequestText.Parse(output, ThisApp));
    }

    [Fact]
    public void ACategoryHoldingNothingButEmptyLines_IsStillAReading()
    {
        var holds = PowerRequestText.Parse("DISPLAY:\nNone.\n\nSYSTEM:\nNone.\n", ThisApp);

        Assert.NotNull(holds);
        Assert.Empty(holds!);
    }

    [Fact]
    public void OnlyTheCategoriesThatHoldTheMachineUp_CountTowardsTheWarning()
    {
        var holds = PowerRequestText.Parse(Sample, ThisApp)!;

        Assert.All(holds.Where(AwakeHoldPolicy.HoldsTheMachineAwake),
                   h => Assert.Contains(h.Category, new[] { "DISPLAY", "SYSTEM", "AWAYMODE", "EXECUTION" }));
        Assert.False(AwakeHoldPolicy.HoldsTheMachineAwake(
            new PowerRequestEntry("PERFBOOST", "PROCESS", "x.exe", null, false)));
        Assert.False(AwakeHoldPolicy.HoldsTheMachineAwake(
            new PowerRequestEntry("ACTIVELOCKSCREEN", "PROCESS", "x.exe", null, false)));
    }

    [Fact]
    public void ThisApplicationsOwnHold_IsNeverWarnedAbout()
    {
        // It knows what it is holding and why, and its Keep Awake and lid-close waits already say so.
        var holds = PowerRequestText.Parse(Sample, ThisApp)!;
        var now   = new System.DateTimeOffset(2026, 9, 20, 12, 0, 0, System.TimeSpan.Zero);
        var since = holds.ToDictionary(AwakeHoldPolicy.Key, _ => now.AddHours(-9));

        var worth = AwakeHoldPolicy.WorthWarningAbout(holds, since, now, System.TimeSpan.FromHours(4));

        Assert.DoesNotContain(worth, h => h.IsThisApplication);
        Assert.Contains(worth, h => h.ShortHolder == "player.exe");
    }

    [Fact]
    public void AHoldYoungerThanTheThreshold_IsNotWarnedAbout()
    {
        var holds = PowerRequestText.Parse(Sample, ThisApp)!;
        var now   = new System.DateTimeOffset(2026, 9, 20, 12, 0, 0, System.TimeSpan.Zero);
        var since = holds.ToDictionary(AwakeHoldPolicy.Key, _ => now.AddHours(-1));

        Assert.Empty(AwakeHoldPolicy.WorthWarningAbout(holds, since, now, System.TimeSpan.FromHours(4)));
    }
}
