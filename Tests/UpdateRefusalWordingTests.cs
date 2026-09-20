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
    /// Scanned over the whole assembly as UTF-16, which is how a literal is stored.</summary>
    [Fact]
    public void NoRefusalWordingClaimsTheFileWasDeleted()
    {
        foreach (var assembly in new[] { typeof(UpdateOptions).Assembly,
                                         typeof(ZeroZero.Update.Win32.UpdateFlow).Assembly })
        {
            var text = Encoding.Unicode.GetString(File.ReadAllBytes(assembly.Location));
            Assert.DoesNotContain("delet", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>A verdict's own detail is the part of a refusal that is readable, and it makes no
    /// claim about deletion either.</summary>
    [Fact]
    public void AVerdictsDetailClaimsNoDeletionEither()
    {
        var signer = new ExpectedSigner(AppUpdates.ExpectedPublisher, null, acceptSelfSignedSubject: true);
        var directory = Path.Combine(Path.GetTempPath(), "ChargeKeeper-Verdicts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var unsigned = Path.Combine(directory, "unsigned.exe");
            File.WriteAllBytes(unsigned, [0x4D, 0x5A, 1, 2, 3, 4]);

            foreach (var result in new[]
            {
                InstallerVerifier.Verify(Path.Combine(directory, "absent.exe"), new string('0', 64), signer),
                InstallerVerifier.Verify(unsigned, InstallerVerifier.Sha256Of(unsigned), signer),
                InstallerVerifier.Verify(unsigned, new string('a', 64), signer),
            })
            {
                Assert.NotEqual(VerificationVerdict.Verified, result.Verdict);
                Assert.DoesNotContain("delet", result.Detail, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* left in the temporary folder */ }
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
