using System.Text.RegularExpressions;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The move of the pre-rename data folder has to run before anything names the new one. The data
/// path comes from the shared library, and asking it for the folder creates the folder; the move
/// then finds its destination present and refuses, stranding the settings and battery history in
/// the old folder for good. Nothing on screen or in a log would say so.
/// </summary>
public class ProgramStartupOrderTests
{
    private static string ProgramSource() => File.ReadAllText(RepoFiles.Find("Program.cs"));

    /// <summary>What in <paramref name="source"/> breaks the order: the data path named in
    /// <c>Main</c> before the move, or named inside the move itself.</summary>
    private static List<string> OrderViolations(string source)
    {
        var violations = new List<string>();

        int main = source.IndexOf("static void Main()", StringComparison.Ordinal);
        int move = source.IndexOf("MigrateLegacyAppDataFolder(", main, StringComparison.Ordinal);
        if (main < 0 || move < 0) return ["Main no longer calls MigrateLegacyAppDataFolder."];

        // Code only: the comment above the call explains the trap by naming the data path.
        string beforeMove = string.Join('\n', source[main..move].Split('\n')
                                  .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        foreach (var name in new[] { "AppPaths", "ProductDataPath", "AppLog", "PowerLog" })
            if (beforeMove.Contains(name, StringComparison.Ordinal))
                violations.Add($"Main names {name} before the legacy folder has moved.");

        var body = Regex.Match(source,
            @"static Action\? MigrateLegacyAppDataFolder\(string appDataRoot\)\s*\{(?<body>.*?)\n    \}",
            RegexOptions.Singleline);
        if (!body.Success) return [.. violations, "MigrateLegacyAppDataFolder's body was not found."];

        foreach (var name in new[] { "AppPaths", "ProductDataPath" })
            if (body.Groups["body"].Value.Contains(name, StringComparison.Ordinal))
                violations.Add($"The move asks {name} for its destination, which creates it.");

        return violations;
    }

    [Fact]
    public void TheLegacyFolderMovesBeforeAnythingNamesTheDataFolder() =>
        Assert.Empty(OrderViolations(ProgramSource()));

    /// <summary>The check above can fail: the data path named at the top of Main is caught.</summary>
    [Fact]
    public void AnEarlierReadOfTheDataFolderIsCaught()
    {
        string source   = ProgramSource();
        int    main     = source.IndexOf("static void Main()", StringComparison.Ordinal);
        int    open     = source.IndexOf('{', main) + 1;
        string mutated  = source.Insert(open, "\n        _ = AppPaths.DataDir;");

        Assert.NotEmpty(OrderViolations(mutated));
    }

    [Fact]
    public void TheDataFolderIsTheSharedProductPath() =>
        Assert.Contains("ProductDataPath.Root(AppInfo.Name)",
                        File.ReadAllText(RepoFiles.Find(Path.Combine("Services", "AppPaths.cs"))),
                        StringComparison.Ordinal);

    [Fact]
    public void TheLegacyFolderMovesWhenTheNewOneIsAbsent()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ck-legacy-move-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "LenovoPowerTray"));
            File.WriteAllText(Path.Combine(root, "LenovoPowerTray", "settings.json"), "{}");

            Assert.NotNull(Program.MigrateLegacyAppDataFolder(root));
            Assert.True(File.Exists(Path.Combine(root, "ChargeKeeper", "settings.json")));
            Assert.False(Directory.Exists(Path.Combine(root, "LenovoPowerTray")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    /// <summary>The refusal the ordering exists for: a destination already present leaves the old
    /// folder where it is.</summary>
    [Fact]
    public void AnExistingDestinationLeavesTheLegacyFolderInPlace()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ck-legacy-move-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "LenovoPowerTray"));
            Directory.CreateDirectory(Path.Combine(root, "ChargeKeeper"));

            Assert.Null(Program.MigrateLegacyAppDataFolder(root));
            Assert.True(Directory.Exists(Path.Combine(root, "LenovoPowerTray")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
