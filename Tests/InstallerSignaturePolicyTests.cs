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

    /// <summary>A pinned thumbprint, taken from the pin list rather than written out again, so a
    /// rotation that changes the list does not need every test edited with it.</summary>
    private static string OurThumbprint => InstallerSignaturePolicy.PinnedSigningThumbprints[0];

    /// <summary>A syntactically valid SHA-256 thumbprint that is not pinned: what a forged
    /// certificate carrying the right subject would present.</summary>
    private const string ForgedThumbprint =
        "0000000000000000000000000000000000000000000000000000000000000001";

    [Fact]
    public void FullyTrustedAndPinnedCertificate_IsAccepted()
    {
        // A machine that does have the studio root installed — a developer box. "Ours" here means
        // the pinned certificate, not merely a matching subject.
        Assert.Equal(InstallerVerdict.Accepted,
                     InstallerSignaturePolicy.Decide(InstallerSignaturePolicy.S_OK, Ours, OurThumbprint));
    }

    [Theory]
    [InlineData(InstallerSignaturePolicy.CERT_E_UNTRUSTEDROOT)]
    [InlineData(InstallerSignaturePolicy.CERT_E_CHAINING)]
    public void UntrustedRootWithThePinnedCertificate_IsAccepted(uint trustResult)
    {
        // The normal case on a user's machine: the release certificate is self-signed, so the chain
        // never validates. Refusing this would block every legitimate update. The tolerance applies
        // to the pinned certificate only — see OurSubjectWithAForgedCertificate_IsRefused for the
        // case it used to let through.
        Assert.Equal(InstallerVerdict.Accepted,
                     InstallerSignaturePolicy.Decide(trustResult, Ours, OurThumbprint));
    }

    [Theory]
    [InlineData(InstallerSignaturePolicy.TRUST_E_NOSIGNATURE)]
    [InlineData(InstallerSignaturePolicy.TRUST_E_SUBJECT_FORM_UNKNOWN)]
    [InlineData(InstallerSignaturePolicy.TRUST_E_PROVIDER_UNKNOWN)]
    public void Unsigned_IsRefused(uint trustResult)
    {
        Assert.Equal(InstallerVerdict.NotSigned,
                     InstallerSignaturePolicy.Decide(trustResult, null, null));
    }

    [Fact]
    public void Unsigned_IsRefusedEvenIfSomeCertificateIsReadable()
    {
        // The trust result decides "not signed" on its own; a subject read from somewhere must not
        // be able to talk the policy round.
        Assert.Equal(InstallerVerdict.NotSigned,
                     InstallerSignaturePolicy.Decide(InstallerSignaturePolicy.TRUST_E_NOSIGNATURE, Ours, OurThumbprint));
    }

    [Fact]
    public void BadDigest_IsRefusedAsTampered_EvenWithOurCertificate()
    {
        // A file altered after signing still carries the real publisher's certificate, so the digest
        // is the only thing that catches it — and it is checked before the publisher comparison.
        Assert.Equal(InstallerVerdict.Tampered,
                     InstallerSignaturePolicy.Decide(InstallerSignaturePolicy.TRUST_E_BAD_DIGEST, Ours, OurThumbprint));
    }

    [Fact]
    public void SignedBySomeoneElse_IsRefused_EvenWhenFullyTrusted()
    {
        // A perfectly valid signature from a real CA is still not our release.
        Assert.Equal(InstallerVerdict.WrongPublisher,
                     InstallerSignaturePolicy.Decide(InstallerSignaturePolicy.S_OK, Impostor, ForgedThumbprint));
    }

    [Fact]
    public void SelfSignedBySomeoneElse_IsRefused()
    {
        // The untrusted-root tolerance must not become a way in for any self-signed file.
        Assert.Equal(InstallerVerdict.WrongPublisher,
                     InstallerSignaturePolicy.Decide(InstallerSignaturePolicy.CERT_E_UNTRUSTEDROOT, Impostor, ForgedThumbprint));
    }

    [Fact]
    public void ExpiredCertificate_IsRefused_EvenThoughItIsOurs()
    {
        Assert.Equal(InstallerVerdict.UntrustedCertificate,
                     InstallerSignaturePolicy.Decide(InstallerSignaturePolicy.CERT_E_EXPIRED, Ours, OurThumbprint));
    }

    [Fact]
    public void AnUnknownTrustFailure_IsRefused_NotAccepted()
    {
        // Fail closed: a result code the policy has never seen must never read as permission.
        Assert.Equal(InstallerVerdict.UntrustedCertificate,
                     InstallerSignaturePolicy.Decide(0x800B0111, Ours, OurThumbprint));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoReadableSigner_IsRefused(string? subject)
    {
        Assert.Equal(InstallerVerdict.Unreadable,
                     InstallerSignaturePolicy.Decide(InstallerSignaturePolicy.S_OK, subject, OurThumbprint));
    }

    [Fact]
    public void PublisherComparisonIgnoresCaseAndSurroundingSpace_ButNothingElse()
    {
        Assert.Equal(InstallerVerdict.Accepted,
                     InstallerSignaturePolicy.Decide(InstallerSignaturePolicy.S_OK,
                                                     "  cn=zerozero software  ", OurThumbprint));
        Assert.Equal(InstallerVerdict.WrongPublisher,
                     InstallerSignaturePolicy.Decide(InstallerSignaturePolicy.S_OK,
                                                     "CN=ZeroZero Software Ltd", OurThumbprint));
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
        Assert.Contains("does not recognise",
                        InstallerSignaturePolicy.MessageFor(InstallerVerdict.UnpinnedCertificate));
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

    // ---- The certificate pin ----------------------------------------------------------------
    //
    // A subject is free text on a self-signed certificate, so the name check alone accepted any
    // self-signed file calling itself CN=ZeroZero Software. The signer's SHA-256 thumbprint has to
    // be one of InstallerSignaturePolicy.PinnedSigningThumbprints as well.

    [Fact]
    public void OurSubjectWithAForgedCertificate_IsRefused()
    {
        // THE case the pin exists for, and the contract that changed: before the pin, a throwaway
        // self-signed certificate whose subject read CN=ZeroZero Software was Accepted. Minting one
        // costs nothing, so the subject alone was never evidence.
        Assert.Equal(InstallerVerdict.UnpinnedCertificate,
                     InstallerSignaturePolicy.Decide(InstallerSignaturePolicy.CERT_E_UNTRUSTEDROOT,
                                                     Ours, ForgedThumbprint));
    }

    [Fact]
    public void OurSubjectWithAForgedCertificate_IsRefusedEvenWhenFullyTrusted()
    {
        // The refusal must not depend on the chain result: a machine that has some root installed
        // must reach the same answer as one that has not.
        Assert.Equal(InstallerVerdict.UnpinnedCertificate,
                     InstallerSignaturePolicy.Decide(InstallerSignaturePolicy.S_OK, Ours, ForgedThumbprint));
    }

    [Fact]
    public void APinnedThumbprintIsRequired_NotMerelyPreferred()
    {
        // Every trust result the policy would otherwise accept still refuses without the pin.
        foreach (var trustResult in new[] { InstallerSignaturePolicy.S_OK,
                                            InstallerSignaturePolicy.CERT_E_UNTRUSTEDROOT,
                                            InstallerSignaturePolicy.CERT_E_CHAINING })
        {
            Assert.Equal(InstallerVerdict.Accepted,
                         InstallerSignaturePolicy.Decide(trustResult, Ours, OurThumbprint));
            Assert.Equal(InstallerVerdict.UnpinnedCertificate,
                         InstallerSignaturePolicy.Decide(trustResult, Ours, ForgedThumbprint));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoReadableThumbprint_IsRefused(string? thumbprint)
    {
        // The certificate parsed far enough to yield a subject but not a hash: fail closed.
        Assert.Equal(InstallerVerdict.Unreadable,
                     InstallerSignaturePolicy.Decide(InstallerSignaturePolicy.S_OK, Ours, thumbprint));
    }

    [Fact]
    public void EveryPinnedThumbprintIsAccepted_SoTheListIsWhatIsConsulted()
    {
        // Walks the pin list rather than naming a literal: a rotation entry added to
        // PinnedSigningThumbprints must be honoured by Decide without any other edit, which a single
        // hard-coded comparison would not do.
        Assert.NotEmpty(InstallerSignaturePolicy.PinnedSigningThumbprints);

        foreach (var pinned in InstallerSignaturePolicy.PinnedSigningThumbprints)
        {
            Assert.True(InstallerSignaturePolicy.IsPinnedThumbprint(pinned));
            Assert.Equal(InstallerVerdict.Accepted,
                         InstallerSignaturePolicy.Decide(InstallerSignaturePolicy.CERT_E_UNTRUSTEDROOT,
                                                         Ours, pinned));
        }
    }

    [Fact]
    public void EveryPinnedThumbprintIsASha256Value()
    {
        // 64 hex characters. A SHA-1 thumbprint is 40 and is what certificate dialogs and
        // X509Certificate2.Thumbprint show, so pasting one in is the likely mistake; it would never
        // match a signer hash and would silently refuse every update.
        foreach (var pinned in InstallerSignaturePolicy.PinnedSigningThumbprints)
        {
            Assert.Equal(64, pinned.Length);
            Assert.All(pinned, c => Assert.True(Uri.IsHexDigit(c), $"'{c}' is not a hex digit."));
        }
    }

    [Fact]
    public void PinnedThumbprintsAreDistinct()
    {
        Assert.Equal(InstallerSignaturePolicy.PinnedSigningThumbprints.Count,
                     InstallerSignaturePolicy.PinnedSigningThumbprints
                         .Select(t => t.ToUpperInvariant()).Distinct().Count());
    }

    [Fact]
    public void ThumbprintComparisonIgnoresCaseAndSeparators_ButNotContent()
    {
        // certutil prints spaces, openssl prints colons, and either case turns up in practice.
        var pinned = OurThumbprint;

        Assert.True(InstallerSignaturePolicy.IsPinnedThumbprint(pinned.ToLowerInvariant()));
        Assert.True(InstallerSignaturePolicy.IsPinnedThumbprint(string.Join(":", Chunk(pinned))));
        Assert.True(InstallerSignaturePolicy.IsPinnedThumbprint(" " + string.Join(" ", Chunk(pinned)) + " "));

        // One digit changed is a different certificate, separators or not.
        var altered = pinned[..^1] + (pinned[^1] == 'A' ? 'B' : 'A');
        Assert.False(InstallerSignaturePolicy.IsPinnedThumbprint(altered));
        // A truncated value must not match by prefix.
        Assert.False(InstallerSignaturePolicy.IsPinnedThumbprint(pinned[..40]));
        Assert.False(InstallerSignaturePolicy.IsPinnedThumbprint(pinned + "00"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("::: :::")]
    public void AnEmptyThumbprintIsNeverPinned(string? thumbprint)
    {
        // Normalising away separators must not turn "nothing" into a match.
        Assert.False(InstallerSignaturePolicy.IsPinnedThumbprint(thumbprint));
    }

    [Fact]
    public void TheSha1ThumbprintOfTheReleaseCertificateIsNotPinned()
    {
        // Measured from the signed ChargeKeeper-Setup-1.10.0.exe: SHA-1 4909D644..., SHA-256
        // 486E2A37.... Pinning the SHA-1 value would be a plausible slip and must not verify.
        Assert.False(InstallerSignaturePolicy.IsPinnedThumbprint("4909D644147756958E31783CF9D5926873522197"));
    }

    private static IEnumerable<string> Chunk(string value) =>
        Enumerable.Range(0, value.Length / 2).Select(i => value.Substring(i * 2, 2));
}
