using System.Collections.Generic;
using System.Linq;
using ChargeKeeper.Services;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The list the picker offers, and the one property that decides whether choosing from it works: an
/// entry has to yield something a firewall rule can be keyed on.
/// </summary>
/// <remarks>Everything here runs against composed entries. Nothing reads this machine, no firewall
/// rule is created, changed or removed, and no settings document is touched.</remarks>
public class ProgramCatalogueTests
{
    private const string Editor = @"C:\Program Files\Example\editor.exe";
    private const string Reader = @"C:\Program Files\Example\reader.exe";

    /// <summary>A Windows Store executable. The firewall keys such a rule on an application
    /// container identifier, not on this path, so an entry naming it would be chosen and blocked
    /// anyway.</summary>
    private const string Packaged = @"C:\Program Files\WindowsApps\Example_1.0.0.0_x64__abc\store.exe";

    /// <summary>The execution alias beside a packaged application: an ordinary-looking path in the
    /// user's own profile that still resolves to the package.</summary>
    private const string Alias = @"C:\Users\Someone\AppData\Local\Microsoft\WindowsApps\store.exe";

    private static ProgramChoice Entry(string path, string name, bool running = false) =>
        new(path, name, running);

    // ── What the picker may offer at all ────────────────────────────────────────────────────────

    /// <summary>The guard that matters most: a person choosing a program from a list and finding it
    /// blocked anyway is the failure this feature cannot afford. A packaged application cannot be
    /// named in the rule shape this application writes, so it is never offered.</summary>
    [Fact]
    public void APackagedApplicationIsNeverOffered()
    {
        Assert.True(ProgramCatalogue.IsPackaged(Packaged));
        Assert.True(ProgramCatalogue.IsPackaged(Alias));
        Assert.False(ProgramCatalogue.IsPackaged(Editor));

        var offered = ProgramCatalogue.Merge(
            [Entry(Packaged, "Store thing", running: true)],
            [Entry(Alias, "Store thing"), Entry(Editor, "Editor")]);

        Assert.Equal([Editor], offered.Select(e => e.Path));
    }

    /// <summary>Everything the picker offers goes on to become a rule naming that path. An entry
    /// that cannot is one the person chooses and never gets.</summary>
    [Fact]
    public void EveryEntryTheListOffers_BecomesARuleNamingThatProgram()
    {
        var offered = ProgramCatalogue.Merge(
            [Entry(Editor, "Editor", running: true)],
            [Entry(Reader, "Reader"), Entry(Packaged, "Store thing"),
             Entry(@"C:\Program Files\Example\notes.txt", "Notes"), Entry("editor.exe", "Relative")]);

        var list = new List<string>();
        foreach (var entry in offered)
            Assert.Equal(FocusAllowVerdict.Added,
                         FocusAllowedPrograms.Add(list, entry.Path, sessionRunning: false));

        var rules = FocusFirewallRules.For("198.51.100.7", 8883, "198.51.100.1", list);
        string[] named = [.. rules.Where(r => r.ApplicationPath.Length > 0)
                                  .Select(r => r.ApplicationPath)];

        Assert.Equal(offered.Count, named.Length);
        Assert.Equal([.. offered.Select(e => e.Path)], named);
    }

    /// <summary>What a chosen row hands back is its path. The name beside it is a label — neither
    /// unique nor stable, and nothing a firewall rule can be keyed on — so it is drawn and
    /// discarded. Read from the shipped source, like the feature's other structural guards: the
    /// window cannot be driven without a display.</summary>
    [Fact]
    public void ThePickerHandsBackThePathAndNeverTheName()
    {
        string source = File.ReadAllText(
            RepoFiles.Find(Path.Combine("UI", "ProgramPickerWindow.xaml.cs")));

        Assert.Contains("_onChosen(chosen.Path)", source, System.StringComparison.Ordinal);
        Assert.DoesNotContain("chosen.Name", source, System.StringComparison.Ordinal);
    }

    // ── Merging the two sources ─────────────────────────────────────────────────────────────────

    [Fact]
    public void AProgramInBothSourcesIsOneRow_KeepingTheStartMenuNameAndTheOpenMark()
    {
        var merged = ProgramCatalogue.Merge(
            [Entry(Editor, "editor", running: true)],
            [Entry(Editor.ToUpperInvariant(), "Example Editor")]);

        var only = Assert.Single(merged);
        Assert.Equal("Example Editor", only.Name);
        Assert.True(only.Running);
    }

    [Fact]
    public void WhatIsOpenSortsAboveWhatIsMerelyInstalled()
    {
        var merged = ProgramCatalogue.Merge(
            [Entry(Reader, "Zed", running: true)],
            [Entry(Editor, "Alpha")]);

        Assert.Equal([Reader, Editor], merged.Select(e => e.Path));
    }

    // ── Narrowing by typing ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void TypingNarrowsOnTheNameAndOnThePath()
    {
        var all = ProgramCatalogue.Merge([], [Entry(Editor, "Alpha"), Entry(Reader, "Beta")]);

        Assert.Equal(2, ProgramCatalogue.Match(all, "").Count);
        Assert.Equal([Editor], ProgramCatalogue.Match(all, "alph").Select(e => e.Path));
        Assert.Equal([Reader], ProgramCatalogue.Match(all, "reader").Select(e => e.Path));
        Assert.Empty(ProgramCatalogue.Match(all, "nothing here"));
    }

    /// <summary>Words typed in any order all have to appear, so a half-remembered name and a
    /// half-remembered folder together find one row.</summary>
    [Fact]
    public void EveryWordTypedHasToAppear()
    {
        var all = ProgramCatalogue.Merge([], [Entry(Editor, "Alpha"), Entry(Reader, "Alpha Beta")]);

        Assert.Equal([Reader], ProgramCatalogue.Match(all, "beta alpha").Select(e => e.Path));
        Assert.Equal(2, ProgramCatalogue.Match(all, "alpha").Count);
    }
}
