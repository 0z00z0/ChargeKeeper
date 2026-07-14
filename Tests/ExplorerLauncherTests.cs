using ChargeKeeper.Helpers;
using Xunit;

namespace ChargeKeeper.Tests;

// Pure argument-building for the "Open settings folder" action relocated from the tray menu into
// the Settings window's Advanced footer (TODO #28).
public class ExplorerLauncherTests
{
    [Fact]
    public void SelectFileArguments_FileExists_SelectsTheFile()
    {
        var args = ExplorerLauncher.SelectFileArguments(@"C:\Users\me\AppData\ChargeKeeper\settings.json", fileExists: true);
        Assert.Equal("/select,\"C:\\Users\\me\\AppData\\ChargeKeeper\\settings.json\"", args);
    }

    [Fact]
    public void SelectFileArguments_FileMissing_OpensContainingFolder()
    {
        var args = ExplorerLauncher.SelectFileArguments(@"C:\Users\me\AppData\ChargeKeeper\settings.json", fileExists: false);
        Assert.Equal("\"C:\\Users\\me\\AppData\\ChargeKeeper\"", args);
    }

    [Fact]
    public void SelectFileArguments_FileMissing_NoDirectory_FallsBackToPath()
    {
        // A bare filename has no directory component — fall back to quoting the path itself rather
        // than emitting an empty "" that would open the user's home/Documents unexpectedly.
        var args = ExplorerLauncher.SelectFileArguments("settings.json", fileExists: false);
        Assert.Equal("\"settings.json\"", args);
    }
}
