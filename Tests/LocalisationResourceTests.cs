using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// Guards the application's own <c>.resw</c> (#152): every <c>x:Uid</c> in the markup has an entry.
/// Whether WinUI applies that entry to a rendered control needs a live window, which an elevated,
/// single-instance app run from a test host cannot stand up; see LOCALISATION.md.
/// </summary>
public class LocalisationResourceTests
{
    /// <summary>Every <c>x:Uid="…"</c> found in the application's own XAML (the shared modules keep
    /// their own resources and are out of scope).</summary>
    private static IEnumerable<(string Uid, string File)> UidsInXaml()
    {
        string root = Path.GetDirectoryName(RepoFiles.Find("ChargeKeeper.csproj"))!;
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "UI"), "*.xaml"))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"x:Uid=""([^""]+)"""))
                yield return (m.Groups[1].Value, Path.GetFileName(file));
    }

    [Fact]
    public void EveryXUidInXamlHasAMatchingReswEntry()
    {
        // The failure mode this guards: a missing .resw entry does not fail the build, it leaves the
        // control's markup-declared value standing (or blank, once that value is removed) — visible
        // only on screen, which this application cannot be watched on. Catch it here instead.
        var doc = XDocument.Load(RepoFiles.Find(Path.Combine("Strings", "en-GB", "Resources.resw")));
        var declaredUids = doc.Root!.Elements("data")
            .Select(d => d.Attribute("name")!.Value.Split('.')[0])
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (uid, file) in UidsInXaml())
            Assert.True(declaredUids.Contains(uid),
                $"{file} declares x:Uid=\"{uid}\" with no matching entry in Resources.resw.");
    }
}
