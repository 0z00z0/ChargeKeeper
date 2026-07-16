using System.Text.RegularExpressions;
using ChargeKeeper.Helpers;
using Xunit;

namespace ChargeKeeper.Tests;

// The studio rule is that every third-party library ships credited with its author and licence, and
// the app states that credit in TWO places: the About box (AboutContent.Build().ExternalLibraries,
// rendered by BrandAboutControl) and the README's "External libraries" table. AboutContent's doc
// comment REQUIRES the two to stay in sync — but nothing enforced it, and they already drifted once
// (MQTTnet's purpose string, fixed in e28186d). These tests make the comment enforceable: edit one
// side only and the build goes red, naming the row that drifted.
public class AboutCreditsTests
{
    // | [Name](url) | Author | Purpose | License |
    // Name is a markdown link in the README (the project URL) but a bare string in AboutInfo, so the
    // link text is what's compared. Purpose/licence are compared verbatim — a reworded purpose on one
    // side only is exactly the drift that got through last time.
    private static readonly Regex RowPattern = new(
        @"^\|\s*\[(?<name>[^\]]+)\]\([^)]*\)\s*\|\s*(?<author>[^|]+?)\s*\|\s*(?<purpose>[^|]+?)\s*\|\s*(?<license>[^|]+?)\s*\|\s*$",
        RegexOptions.Compiled);

    private sealed record Credit(string Name, string Author, string Purpose, string License)
    {
        public override string ToString() => $"{Name} | {Author} | {Purpose} | {License}";
    }

    /// <summary>
    /// Walks up from the test assembly to the repo root. The test binary lives under
    /// Tests\bin\&lt;config&gt;\&lt;tfm&gt;\, and that depth is not worth hard-coding — probing for the
    /// marker file survives any future change to the output layout.
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
    /// Parses the rows of the README's "External libraries" table — the block between that heading
    /// and the next one. Scoped to that section rather than scanning every table in the file so an
    /// unrelated table added later can't silently join the comparison set.
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
        // library. Reported per-side: which row the README claims that the About box does not, and
        // vice versa — that names the exact drifting row instead of dumping two lists.
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
        // The credit is the point of the list; a blank author or licence would satisfy the set-equality
        // test above (both sides blank) while failing the rule it exists to enforce.
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
