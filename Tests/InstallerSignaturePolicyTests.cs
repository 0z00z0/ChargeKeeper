using ChargeKeeper.Helpers;
using Xunit;

namespace ChargeKeeper.Tests;

/// <summary>
/// The launch/refuse decision for a downloaded installer. Pure, so every case is covered here
/// rather than only by actually downloading a tampered file: the two inputs are what
/// WinVerifyTrust concluded and who signed.
/// </summary>
public class InstallerSignaturePolicyTests
{
    private const string Ours     = InstallerSignaturePolicy.ExpectedPublisher;
    private const string Impostor = "CN=Someone Else, O=Somewhere";

    [Fact]
    public void FullyTrustedAndOurs_IsAccepted()
    {
        // A machine that does have the studio root installed — a developer box.
        Assert.Equal(InstallerVerdict.Accepted,
                     InstallerSignaturePolicy.Decide(InstallerSignaturePolicy.S_OK, Ours));
    }

    [Theory]
    [InlineData(InstallerSignaturePolicy.CERT_E_UNTRUSTEDROOT)]
    [InlineData(InstallerSignaturePolicy.CERT_E_CHAINING)]
    public void UntrustedRootButOurs_IsAccepted(uint trustResult)
    {
        // The normal case on a user's machine: the release certificate is self-signed, so the chain
        // never validates. Refusing this would block every legitimate update.
        Assert.Equal(InstallerVerdict.Accepted, InstallerSignaturePolicy.Decide(trustResult, Ours));
    }

    [Theory]
    [InlineData(InstallerSignaturePolicy.TRUST_E_NOSIGNATURE)]
    [InlineData(InstallerSignaturePolicy.TRUST_E_SUBJECT_FORM_UNKNOWN)]
    [InlineData(InstallerSignaturePolicy.TRUST_E_PROVIDER_UNKNOWN)]
    public void Unsigned_IsRefused(uint trustResult)
    {
        Assert.Equal(InstallerVerdict.NotSigned, InstallerSignaturePolicy.Decide(trustResult, null));
    }

    [Fact]
    public void Unsigned_IsRefusedEvenIfSomeCertificateIsReadable()
    {
        // The trust result decides "not signed" on its own; a subject read from somewhere must not
        // be able to talk the policy round.
        Assert.Equal(InstallerVerdict.NotSigned,
                     InstallerSignaturePolicy.Decide(InstallerSignaturePolicy.TRUST_E_NOSIGNATURE, Ours));
    }

    [Fact]
    public void BadDigest_IsRefusedAsTampered_EvenWithOurCertificate()
    {
        // A file altered after signing still carries the real publisher's certificate, so the digest
        // is the only thing that catches it — and it is checked before the publisher comparison.
        Assert.Equal(InstallerVerdict.Tampered,
                     InstallerSignaturePolicy.Decide(InstallerSignaturePolicy.TRUST_E_BAD_DIGEST, Ours));
    }

    [Fact]
    public void SignedBySomeoneElse_IsRefused_EvenWhenFullyTrusted()
    {
        // A perfectly valid signature from a real CA is still not our release.
        Assert.Equal(InstallerVerdict.WrongPublisher,
                     InstallerSignaturePolicy.Decide(InstallerSignaturePolicy.S_OK, Impostor));
    }

    [Fact]
    public void SelfSignedBySomeoneElse_IsRefused()
    {
        // The untrusted-root tolerance must not become a way in for any self-signed file.
        Assert.Equal(InstallerVerdict.WrongPublisher,
                     InstallerSignaturePolicy.Decide(InstallerSignaturePolicy.CERT_E_UNTRUSTEDROOT, Impostor));
    }

    [Fact]
    public void ExpiredCertificate_IsRefused_EvenThoughItIsOurs()
    {
        Assert.Equal(InstallerVerdict.UntrustedCertificate,
                     InstallerSignaturePolicy.Decide(InstallerSignaturePolicy.CERT_E_EXPIRED, Ours));
    }

    [Fact]
    public void AnUnknownTrustFailure_IsRefused_NotAccepted()
    {
        // Fail closed: a result code the policy has never seen must never read as permission.
        Assert.Equal(InstallerVerdict.UntrustedCertificate,
                     InstallerSignaturePolicy.Decide(0x800B0111, Ours));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoReadableSigner_IsRefused(string? subject)
    {
        Assert.Equal(InstallerVerdict.Unreadable,
                     InstallerSignaturePolicy.Decide(InstallerSignaturePolicy.S_OK, subject));
    }

    [Fact]
    public void PublisherComparisonIgnoresCaseAndSurroundingSpace_ButNothingElse()
    {
        Assert.Equal(InstallerVerdict.Accepted,
                     InstallerSignaturePolicy.Decide(InstallerSignaturePolicy.S_OK, "  cn=zerozero software  "));
        Assert.Equal(InstallerVerdict.WrongPublisher,
                     InstallerSignaturePolicy.Decide(InstallerSignaturePolicy.S_OK, "CN=ZeroZero Software Ltd"));
    }

    [Fact]
    public void AcceptedIsNotTheDefaultValue()
    {
        // A default-constructed verdict must not read as permission to run an executable.
        Assert.NotEqual(InstallerVerdict.Accepted, default(InstallerVerdict));
    }

    [Fact]
    public void EveryRefusalSaysTheFileWasNotRun_AndNoneClaimsItWasDeleted()
    {
        foreach (var verdict in Enum.GetValues<InstallerVerdict>())
        {
            if (verdict == InstallerVerdict.Accepted) continue;

            var message = InstallerSignaturePolicy.MessageFor(verdict);
            Assert.Contains("NOT been run", message);
            Assert.Contains("releases page", message);
            // Deletion is best-effort at the call site, so the message must not assert it happened.
            Assert.DoesNotContain("deleted", message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void EachRefusalNamesItsOwnReason()
    {
        var messages = Enum.GetValues<InstallerVerdict>()
                           .Where(v => v != InstallerVerdict.Accepted)
                           .Select(InstallerSignaturePolicy.MessageFor)
                           .ToList();

        Assert.Equal(messages.Count, messages.Distinct().Count());
        Assert.Contains("not digitally signed",
                        InstallerSignaturePolicy.MessageFor(InstallerVerdict.NotSigned));
        Assert.Contains("damaged or has been altered",
                        InstallerSignaturePolicy.MessageFor(InstallerVerdict.Tampered));
        Assert.Contains("signed by someone other than ZeroZero Software",
                        InstallerSignaturePolicy.MessageFor(InstallerVerdict.WrongPublisher));
    }

    [Fact]
    public void EachDownloadPathIsFresh_AndUnderItsOwnDirectory()
    {
        // The predictable %TEMP%\ChargeKeeper-Setup.exe this replaced was written and then executed,
        // so whatever could occupy that name first decided what got launched.
        var first  = InstallerSignature.NewDownloadPath();
        var second = InstallerSignature.NewDownloadPath();

        try
        {
            Assert.NotEqual(first, second);
            Assert.NotEqual(Path.GetDirectoryName(first), Path.GetDirectoryName(second));
            Assert.NotEqual(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar),
                            Path.GetDirectoryName(first));
            Assert.True(Directory.Exists(Path.GetDirectoryName(first)));
        }
        finally
        {
            InstallerSignature.Discard(first);
            InstallerSignature.Discard(second);
        }
    }

    [Fact]
    public void Discard_RemovesThePerRunDirectory()
    {
        var path = InstallerSignature.NewDownloadPath();
        File.WriteAllText(path, "not really an installer");

        InstallerSignature.Discard(path);

        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
    }

    [Fact]
    public void Verify_RefusesAnUnsignedFile()
    {
        // The end-to-end half that needs no fixture: an unsigned file must never come back Accepted.
        var path = InstallerSignature.NewDownloadPath();
        try
        {
            File.WriteAllBytes(path, new byte[] { 0x4D, 0x5A, 0x90, 0x00 });
            Assert.NotEqual(InstallerVerdict.Accepted, InstallerSignature.Verify(path));
        }
        finally
        {
            InstallerSignature.Discard(path);
        }
    }

    [Fact]
    public void Verify_RefusesAMissingFile()
    {
        // Only "not launchable" is asserted, not the exact verdict: WinVerifyTrust reaches the same
        // refusal for a path that does not exist, so pinning Unreadable here would pin the interop's
        // behaviour rather than the guard's.
        Assert.False(InstallerSignaturePolicy.MayLaunch(
            InstallerSignature.Verify(Path.Combine(Path.GetTempPath(),
                                                   "ChargeKeeper-does-not-exist-" + Guid.NewGuid() + ".exe"))));
    }

    [Fact]
    public void MayLaunch_IsTrueForAcceptedAndNothingElse()
    {
        // The gate the tray menu asks before handing the file to the shell.
        foreach (var verdict in Enum.GetValues<InstallerVerdict>())
            Assert.Equal(verdict == InstallerVerdict.Accepted, InstallerSignaturePolicy.MayLaunch(verdict));
    }

    [Fact]
    public void MayLaunch_RefusesAVerdictValueThatDoesNotExist()
    {
        // Fail closed on a cast that never came from Decide — e.g. a default(int) crossing a boundary.
        Assert.False(InstallerSignaturePolicy.MayLaunch((InstallerVerdict)0));
        Assert.False(InstallerSignaturePolicy.MayLaunch((InstallerVerdict)99));
    }
}
