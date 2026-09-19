using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The shared lifecycle package relaunches the application after a clean exit nobody asked for, but
/// only once its <c>ProcessLifecycle</c> is constructed and armed. ChargeKeeper takes the package for
/// its lock and data path alone: the scheduled watchdog task is the only thing that brings a stopped
/// app back, and a second, in-process relaunch beside it is not wanted.
/// </summary>
public class SharedRelaunchTests
{
    /// <summary>The application's own source files that name the relaunch type.</summary>
    private static string[] FilesNamingTheRelaunch(IEnumerable<(string Name, string Text)> sources) =>
        sources.Where(source => source.Text.Contains("ProcessLifecycle", StringComparison.Ordinal))
               .Select(source => source.Name)
               .ToArray();

    private static IEnumerable<(string Name, string Text)> ApplicationSources()
    {
        string root = Path.GetDirectoryName(RepoFiles.Find("ChargeKeeper.csproj"))!;
        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file);
            string top      = relative.Split(Path.DirectorySeparatorChar)[0];
            if (top is "Tests" or "bin" or "obj" or "publish") continue;
            yield return (relative, File.ReadAllText(file));
        }
    }

    [Fact]
    public void NothingInTheApplicationArmsTheSharedRelaunch() =>
        Assert.Empty(FilesNamingTheRelaunch(ApplicationSources()));

    /// <summary>The check above can fail: a file constructing the type is caught.</summary>
    [Fact]
    public void ASourceConstructingTheRelaunchIsCaught() =>
        Assert.NotEmpty(FilesNamingTheRelaunch(
            [("App.xaml.cs", "var lifecycle = new ProcessLifecycle(options); lifecycle.Arm();")]));
}
