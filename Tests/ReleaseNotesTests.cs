using System.Text.RegularExpressions;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The release body the update flow reads. The release notes reach it through the release workflow,
/// and the installer hash has to survive that route.
/// </summary>
public class ReleaseNotesTests
{
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
