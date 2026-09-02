using ChargeKeeper.Helpers;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The install folders the application is willing to recognise. Both the Watchdog's registration
/// gate and the sweep of the retired folder rest on these, and each is a decision about a real
/// directory on someone's machine, so the near-misses matter as much as the hits.
/// </summary>
public class InstallLocationsTests
{
    private const string Programs = @"C:\Users\Someone\AppData\Local\Programs";
    private const string Current  = Programs + @"\ChargeKeeper";
    private const string Legacy   = Programs + @"\Lenovo Power Tray";

    [Theory]
    [InlineData(Current)]
    [InlineData(Legacy)]
    [InlineData(@"D:\Elsewhere\ChargeKeeper")]
    public void AnExecutableInEitherInstallFolder_IsAnInstalledOne(string dir) =>
        Assert.True(InstallLocations.IsInstalledExe(dir + @"\ChargeKeeper.exe"));

    [Theory]
    [InlineData(@"C:\repo\bin\x64\Debug\net10.0-windows\ChargeKeeper.exe")]   // a build output
    [InlineData(Programs + @"\NotChargeKeeper\ChargeKeeper.exe")]             // folder merely ends with the name
    [InlineData(Programs + @"\ChargeKeeper Old\ChargeKeeper.exe")]            // and merely starts with it
    [InlineData(Current + @"\ChargeKeeper.dll")]                              // right folder, wrong file
    [InlineData(Current + @"\tools\ChargeKeeper.exe")]                        // one level too deep
    [InlineData("")]
    [InlineData(null)]
    public void AnythingElse_IsNot(string? exe) => Assert.False(InstallLocations.IsInstalledExe(exe));

    [Fact]
    public void FolderMatchingIgnoresCase_BecauseWindowsPathsDo()
    {
        Assert.True(InstallLocations.IsProductInstallDir(Programs + @"\CHARGEKEEPER"));
        Assert.True(InstallLocations.IsLegacyInstallDir(Programs + @"\lenovo power tray"));
    }

    [Fact]
    public void ATrailingSeparator_DoesNotHideTheFolderName()
    {
        Assert.True(InstallLocations.IsProductInstallDir(Current + @"\"));
        Assert.True(InstallLocations.IsLegacyInstallDir(Legacy + @"\"));
    }

    [Fact]
    public void TheRetiredFolder_IsComposedAsTheSiblingOfTheCurrentOne() =>
        Assert.Equal(Legacy, InstallLocations.LegacySiblingOf(Current));

    [Theory]
    [InlineData(Legacy)]                       // an installation that has not moved yet
    [InlineData(@"C:\repo\bin\x64\Debug")]     // a build output
    [InlineData("")]
    [InlineData(null)]
    public void NoSiblingIsComposedForAnythingButTheCurrentInstallFolder(string? dir) =>
        Assert.Null(InstallLocations.LegacySiblingOf(dir));
}
