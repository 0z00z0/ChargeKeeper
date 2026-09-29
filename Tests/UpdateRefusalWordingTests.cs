using System.Text;
using ChargeKeeper.Services;
using Xunit;
using ZeroZero.Update;

namespace ChargeKeeper.Tests;

/// <summary>
/// What a refused installer may say, and the two option values a wrong answer would turn into a
/// silent update outage. The refusal text is composed inside the shared component's dialog call and
/// cannot be read back, so the claim is pinned against the shipped assemblies themselves.
/// </summary>
public class UpdateRefusalWordingTests
{
    /// <summary>Removing a refused download is best effort, so no wording may promise it happened.
    /// Scanned over the whole assembly as UTF-16, which is how a literal is stored. The component's
    /// window carries every word a person reads, so it is scanned with the other two.</summary>
    [Fact]
    public void NoRefusalWordingClaimsTheFileWasDeleted()
    {
        foreach (var assembly in new[] { typeof(UpdateOptions).Assembly,
                                         typeof(ZeroZero.Update.Win32.UpdateFlow).Assembly,
                                         typeof(ZeroZero.Update.WinUI.UpdateWindowPrompts).Assembly })
        {
            var text = Encoding.Unicode.GetString(File.ReadAllBytes(assembly.Location));
            Assert.DoesNotContain("delet", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>The asset match is exact and case-sensitive, and the release's own file is version
    /// stamped, so losing the placeholder refuses every update with the release carrying no such
    /// file.</summary>
    [Fact]
    public void TheInstallerAssetNameCarriesTheVersionPlaceholder()
    {
        Assert.Equal("ChargeKeeper-Setup-{version}.exe", AppUpdates.InstallerAssetName);

        var release = new ReleaseInfo("v9.9.9", new Version(9, 9, 9), "9.9.9", "", "",
                                      new Uri("https://example.invalid"), null,
                                      [new ReleaseAsset("ChargeKeeper-Setup-9.9.9.exe", 1,
                                                        new Uri("https://example.invalid/a"))]);
        Assert.Null(release.FindAsset(AppUpdates.InstallerAssetName));
        Assert.NotNull(release.FindAsset("ChargeKeeper-Setup-9.9.9.exe"));
    }

    /// <summary>The release certificate is self-signed, so without this opt-in every download is
    /// refused on an untrusted chain.</summary>
    [Fact]
    public void TheSelfSignedPublisherNameIsAccepted()
    {
        var signer = new ExpectedSigner(AppUpdates.ExpectedPublisher, null, acceptSelfSignedSubject: true);
        Assert.True(signer.AcceptsSelfSignedSubject);
        Assert.Empty(signer.CertificateThumbprints);
        Assert.Equal("CN=ZeroZero Software", AppUpdates.ExpectedPublisher);
    }
}
