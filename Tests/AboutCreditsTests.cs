using System.Text.RegularExpressions;
using ChargeKeeper.Helpers;
using Xunit;

namespace ChargeKeeper.Tests;

// Every third-party library ships credited with its author and licence, stated in two places: the
// About box and the README's "External libraries" table. These tests make that requirement
// enforceable — edit one side only and the build goes red, naming the row that drifted.
public class AboutCreditsTests
{
    // | [Name](url) | Author | Purpose | Licence |
    // Name is a markdown link in the README but a bare string in AboutInfo, so the link text is what
    // is compared; purpose and licence are compared verbatim.
    private static readonly Regex RowPattern = new(
        @"^\|\s*\[(?<name>[^\]]+)\]\([^)]*\)\s*\|\s*(?<author>[^|]+?)\s*\|\s*(?<purpose>[^|]+?)\s*\|\s*(?<license>[^|]+?)\s*\|\s*$",
        RegexOptions.Compiled);

    private sealed record Credit(string Name, string Author, string Purpose, string License)
    {
        public override string ToString() => $"{Name} | {Author} | {Purpose} | {License}";
    }

    /// <summary>
    /// Walks up from the test assembly to the repo root, probing for the marker file rather than
    /// hard-coding the output depth.
    /// </summary>
    private static string FindReadme()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "README.md");
            if (File.Exists(candidate) && File.Exists(Path.Combine(dir.FullName, "ChargeKeeper.csproj")))
                return candidate;
        }

        throw new FileNotFoundException(
            $"Could not locate the repo's README.md walking up from '{AppContext.BaseDirectory}'.");
    }

    /// <summary>
    /// Parses the rows of the README's "External libraries" table. Scoped to that section rather
    /// than every table in the file, so an unrelated table cannot join the comparison set.
    /// </summary>
    private static List<Credit> ReadReadmeCredits()
    {
        string[] lines = File.ReadAllLines(FindReadme());

        int start = Array.FindIndex(lines, l => l.Trim() == "## External libraries");
        Assert.True(start >= 0, "README.md no longer has an '## External libraries' heading.");

        var credits = new List<Credit>();
        for (int i = start + 1; i < lines.Length && !lines[i].StartsWith("## ", StringComparison.Ordinal); i++)
        {
            var m = RowPattern.Match(lines[i].Trim());
            if (m.Success)
                credits.Add(new Credit(m.Groups["name"].Value.Trim(),
                                       m.Groups["author"].Value.Trim(),
                                       m.Groups["purpose"].Value.Trim(),
                                       m.Groups["license"].Value.Trim()));
        }

        Assert.NotEmpty(credits);   // a regex that silently matched nothing would pass every assert below
        return credits;
    }

    private static List<Credit> AboutCredits() =>
        [.. AboutContent.Build().ExternalLibraries
            .Select(l => new Credit(l.Name, l.Author, l.Purpose, l.License))];

    [Fact]
    public void ReadmeTableAndAboutBoxCreditTheSameLibraries()
    {
        var readme = ReadReadmeCredits();
        var about  = AboutCredits();

        // Compared as whole rows so a drifting purpose or licence is caught, not just a missing
        // library, and reported per side so the failure names the row rather than dumping two lists.
        var onlyInReadme = readme.Except(about).ToList();
        var onlyInAbout  = about.Except(readme).ToList();

        Assert.True(onlyInReadme.Count == 0 && onlyInAbout.Count == 0,
            "The README's 'External libraries' table and AboutContent.Build().ExternalLibraries have " +
            "drifted. Every third-party library must be credited identically (name, author, purpose, " +
            "licence) in both.\n" +
            $"In README.md but not in AboutContent:\n  {FormatRows(onlyInReadme)}\n" +
            $"In AboutContent but not in README.md:\n  {FormatRows(onlyInAbout)}");
    }

    [Fact]
    public void EveryCreditedLibraryNamesAnAuthorAndLicence() =>
        // A blank author or licence satisfies the set-equality test above, both sides being blank,
        // while failing the rule that test exists to enforce.
        Assert.All(AboutContent.Build().ExternalLibraries, lib =>
        {
            Assert.False(string.IsNullOrWhiteSpace(lib.Author),  $"{lib.Name} is credited with no author.");
            Assert.False(string.IsNullOrWhiteSpace(lib.License), $"{lib.Name} is credited with no licence.");
            Assert.False(string.IsNullOrWhiteSpace(lib.Url),     $"{lib.Name} is credited with no URL.");
        });

    [Fact]
    public void CreditedLibrariesAreListedOnce() =>
        // Duplicate names would make Except() pass while the About box renders the row twice.
        Assert.Empty(AboutCredits().GroupBy(c => c.Name).Where(g => g.Count() > 1).Select(g => g.Key));

    private static string FormatRows(List<Credit> rows) =>
        rows.Count == 0 ? "(none)" : string.Join("\n  ", rows);
}
