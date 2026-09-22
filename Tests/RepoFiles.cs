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

    /// <summary>The body of a method in a code-behind, from its signature to the matching closing
    /// brace. Several suites assert against one method's text rather than the whole file.</summary>
    public static string MethodBody(string relativePath, string signatureFragment)
    {
        string source = File.ReadAllText(Find(relativePath));
        int start = source.IndexOf(signatureFragment, StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidOperationException($"{signatureFragment} is no longer declared in {relativePath}.");

        int open = source.IndexOf('{', start);
        if (open <= start)
            throw new InvalidOperationException($"{signatureFragment} in {relativePath} has no body.");

        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }

        throw new InvalidOperationException($"{signatureFragment} in {relativePath} has an unbalanced body.");
    }
}
