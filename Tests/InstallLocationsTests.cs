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
    private const string Legacy   = Programs + @"\Lenovo Power Tray";

    [Theory]
    [InlineData(Legacy)]                       // an installation that has not moved yet
    [InlineData(@"C:\repo\bin\x64\Debug")]     // a build output
    public void NoSiblingIsComposedForAnythingButTheCurrentInstallFolder(string? dir) =>
        Assert.Null(InstallLocations.LegacySiblingOf(dir));
}
