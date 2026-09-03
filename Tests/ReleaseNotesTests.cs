using System.Text.RegularExpressions;
using ChargeKeeper.Helpers;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The "What's new" report. Its source is one file, shared with the release workflow, so what a
/// person reads in the application and what they read on the releases page cannot drift; these pin
/// that the file ships, that the running version is named in it, and that the report is reachable
/// from all three places the requirement lists.
/// </summary>
public class ReleaseNotesTests
{
    private static string RepoNotes() => File.ReadAllText(RepoFiles.Find("RELEASE-NOTES.md"));

    [Fact]
    public void TheNotesShipInsideTheAssembly()
    {
        // Bundled rather than fetched: the requirement is that the report is always reachable, and
        // an "always" that depends on a network is not one.
        string embedded = ReleaseNotes.ReadDocument();
        Assert.False(string.IsNullOrWhiteSpace(embedded),
                     $"The resource {ReleaseNotes.ResourceName} is not embedded in the assembly.");
    }

    [Fact]
    public void TheEmbeddedCopyIsTheRepositorysFile() =>
        // One text, not two: a copy that drifted would be the one nobody was reading.
        Assert.Equal(RepoNotes().Replace("\r\n", "\n"), ReleaseNotes.ReadDocument().Replace("\r\n", "\n"));

    [Fact]
    public void TheVersionBeingBuiltHasAnEntry()
    {
        // A release with no entry publishes a body built from commit subjects and shows a report
        // that says nothing, which is the failure this file exists to stop.
        var note = ReleaseNotes.For(AppInfo.Version);
        Assert.True(note is not null, $"RELEASE-NOTES.md names no section for {AppInfo.Version}.");
        Assert.NotEmpty(note!.Lines);
    }

    [Fact]
    public void TheNewestEntryIsTheVersionBeingBuilt() =>
        // Newest first, so the report opens on what just changed rather than on history.
        Assert.Equal(AppInfo.Version, ReleaseNotes.All[0].Version);

    [Fact]
    public void EveryEntryNamesTheIssueItCloses()
    {
        // One sentence per issue, naming the issue number: an entry with no number is a change
        // nobody can trace back. The one exception the format allows is a version whose changes
        // carried no issue at all, which collapses into a single closing line.
        foreach (var note in ReleaseNotes.All)
        {
            if (note.Lines.Count <= 1) continue;   // the collapsed closing line

            foreach (string line in note.Lines)
                Assert.True(Regex.IsMatch(line, @"#\d+"),
                            $"An entry under {note.Version} names no issue: {line}");
        }
    }

    [Fact]
    public void AWrappedEntryIsOneLineInTheReport()
    {
        const string document = """
            # Preamble, which is not part of any version.

            ## 2.0.0

            - #1 A sentence that runs past the column the file wraps at
              and continues on the next line.
            - #2 A short one.

            ## 1.0.0

            - #3 Older.
            """;

        var notes = ReleaseNotes.Parse(document);

        Assert.Equal(2, notes.Count);
        Assert.Equal("2.0.0", notes[0].Version);
        Assert.Equal("#1 A sentence that runs past the column the file wraps at and continues on the next line.",
                     notes[0].Lines[0]);
        Assert.Equal("#2 A short one.", notes[0].Lines[1]);
        Assert.Equal(["#3 Older."], notes[1].Lines);
    }

    [Fact]
    public void AnEmptyDocumentIsNoEntriesRatherThanAThrow() =>
        Assert.Empty(ReleaseNotes.Parse(string.Empty));

    // The three places the report has to be reachable from.

    [Fact]
    public void TheTrayMenuCarriesIt()
    {
        string source = File.ReadAllText(RepoFiles.Find(Path.Combine("UI", "TrayMenu.cs")));
        Assert.Contains("What's new…", source, StringComparison.Ordinal);
        Assert.Contains("ShowWhatsNew", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BothAboutSurfacesCarryIt()
    {
        // The standalone window and the Settings page each host the shared About control, and each
        // needs its own way through: a report reachable from one of the two is not "always".
        Assert.Contains("WhatsNewButton",
                        File.ReadAllText(RepoFiles.Find(Path.Combine("UI", "AboutWindow.xaml"))),
                        StringComparison.Ordinal);
        Assert.Contains("WhatsNewButton",
                        File.ReadAllText(RepoFiles.Find(Path.Combine("UI", "SettingsWindow.xaml"))),
                        StringComparison.Ordinal);
    }

    [Fact]
    public void TheReportIsNotShownOnAFirstInstall()
    {
        // There is no version a first install replaced, so there is nothing to report; the version
        // is still recorded, or the next start would report on a version it had already run.
        string body = SourceMethods.Body(
            Regex.Replace(File.ReadAllText(RepoFiles.Find("App.xaml.cs")), @"//[^\r\n]*", string.Empty),
            "ReportWhatsNewIfTheVersionMoved");

        int record = body.IndexOf("LastSeenVersion = running", StringComparison.Ordinal);
        int guard  = body.IndexOf("seen.Length == 0", StringComparison.Ordinal);
        Assert.True(record >= 0, "The version last run is no longer recorded.");
        Assert.True(guard  >= 0, "A first install would now be shown a report.");
        Assert.True(record < guard,
            "The version is recorded after the first-install guard, so a first install would report again on its next start.");
    }

    [Fact]
    public void TheReleaseWorkflowReadsTheSameFile()
    {
        // Two hand-maintained copies of one text drift, and the copy nobody reads is the one that
        // goes wrong.
        string workflow = File.ReadAllText(RepoFiles.Find(Path.Combine(".github", "workflows", "release.yml")));
        Assert.Contains("RELEASE-NOTES.md", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReleaseBodyStillCarriesTheInstallerHash()
    {
        // The update flow reads the hash as the one distinct sixty-four-character run in the body,
        // so a notes route that dropped it would stop every update.
        string workflow = File.ReadAllText(RepoFiles.Find(Path.Combine(".github", "workflows", "release.yml")));
        int occurrences = Regex.Matches(workflow, @"SHA256 \(installer\)").Count;
        Assert.True(occurrences >= 2,
                    $"The hash line appears on {occurrences} of the release-notes routes; both need it.");
    }
}
