using Xunit;

namespace ChargeKeeper.Tests;

public class InstallerStaleFileTests
{
    [Fact]
    public void Install_RemovesLeftoverSharedLibraryResourceIndexes()
    {
        // A leftover ZeroZero.Mqtt.WinUI.pri crashes the application when Settings opens.
        string script = File.ReadAllText(RepoFiles.Find(Path.Combine("installer", "ChargeKeeper.iss")));
        Assert.Contains(@"Type: files; Name: ""{app}\ZeroZero.*.pri""", script, StringComparison.Ordinal);
    }
}
