namespace ChargeKeeper.Tests;

/// <summary>
/// Locates a file inside the source tree from a test run. Several suites assert against shipped
/// source — markup, the icon vector, the logging config — and each needs the same walk.
/// </summary>
internal static class RepoFiles
{
    /// <summary>Probes upwards for the repo marker rather than hard-coding the test output's depth.</summary>
    public static string Find(string relativePath)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate) && File.Exists(Path.Combine(dir.FullName, "ChargeKeeper.csproj")))
                return candidate;
        }

        throw new FileNotFoundException(
            $"Could not locate '{relativePath}' walking up from '{AppContext.BaseDirectory}'.");
    }
}
