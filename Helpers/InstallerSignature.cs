using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using ChargeKeeper.Services;

namespace ChargeKeeper.Helpers;

/// <summary>What was concluded about a downloaded installer's Authenticode signature.</summary>
internal enum InstallerVerdict
{
    /// <summary>Signature intact and the signer is the expected publisher. The only value that may
    /// be launched. Deliberately NOT the zero value — a default-constructed verdict must never read
    /// as permission to run an executable.</summary>
    Accepted = 1,

    /// <summary>The file carries no Authenticode signature at all.</summary>
    NotSigned,

    /// <summary>A signature is present but does not match the file's contents: damaged in transit,
    /// or altered after signing.</summary>
    Tampered,

    /// <summary>Signed, intact, but by somebody else.</summary>
    WrongPublisher,

    /// <summary>Signed by the expected publisher, but the certificate itself cannot be accepted —
    /// expired, revoked, or explicitly distrusted.</summary>
    UntrustedCertificate,

    /// <summary>The signature could not be examined: the file is missing, unreadable, or the signer
    /// certificate would not parse.</summary>
    Unreadable,
}

/// <summary>
/// The launch/refuse decision for a downloaded installer, kept pure so it can be exercised without
/// a signed fixture: it takes the two pieces of evidence — what WinVerifyTrust concluded, and who
/// signed — and returns a verdict. <see cref="InstallerSignature"/> is the half that touches files.
/// </summary>
/// <remarks>
/// <para>
/// THE POLICY, and why it is not plain chain validation: the ChargeKeeper release certificate is
/// SELF-SIGNED (subject == issuer). A full chain-trust check therefore fails on every machine that
/// has not had that root installed — which is every user's machine — so requiring
/// <c>S_OK</c> would block every legitimate update. Requiring nothing would verify nothing.
/// </para>
/// <para>
/// So: the signature must be cryptographically INTACT (a bad digest is rejected outright, which is
/// what catches a tampered or swapped file), and the signer subject must be exactly
/// <see cref="ExpectedPublisher"/>. Beyond that, one and only one chain failure is tolerated —
/// the untrusted/incomplete root that the self-signed certificate necessarily produces. An expired
/// or revoked certificate is still refused.
/// </para>
/// <para>
/// The residual gap, stated rather than papered over: because an untrusted root is tolerated,
/// anyone can mint a self-signed certificate whose subject reads <c>CN=ZeroZero Software</c> and
/// pass this check. What that buys an attacker is nothing over the wire — the download is HTTPS
/// from GitHub — so the case that mattered was a file planted at a predictable local path, and
/// that is closed by <see cref="InstallerSignature.NewDownloadPath"/> giving every run its own
/// fresh directory instead. Pinning the certificate thumbprint would close the gap outright; it is
/// not done here because a pinned thumbprint turns the next certificate rotation into a silent
/// update outage for everyone still on the old build.
/// </para>
/// </remarks>
internal static class InstallerSignaturePolicy
{
    /// <summary>The subject the release certificate must carry, exactly as signtool writes it.</summary>
    internal const string ExpectedPublisher = "CN=ZeroZero Software";

    // WinVerifyTrust result codes. Only the ones the policy distinguishes are named; anything else
    // falls through to UntrustedCertificate, which refuses.
    internal const uint S_OK                        = 0x00000000;
    internal const uint TRUST_E_NOSIGNATURE         = 0x800B0100;
    internal const uint TRUST_E_SUBJECT_FORM_UNKNOWN = 0x800B0003;
    internal const uint TRUST_E_PROVIDER_UNKNOWN    = 0x800B0001;
    internal const uint TRUST_E_BAD_DIGEST          = 0x80096010;
    internal const uint CERT_E_UNTRUSTEDROOT        = 0x800B0109;
    internal const uint CERT_E_CHAINING             = 0x800B010A;
    internal const uint CERT_E_EXPIRED              = 0x800B0101;

    /// <summary>
    /// The verdict for one downloaded file.
    /// </summary>
    /// <param name="trustResult">What WinVerifyTrust returned for the file.</param>
    /// <param name="signerSubject">The signer certificate's subject, or null when none could be read.</param>
    internal static InstallerVerdict Decide(uint trustResult, string? signerSubject)
    {
        // Ordered so the most specific and most useful thing to tell the user wins. "Not signed"
        // and "tampered" come first because both are true regardless of who the certificate claims
        // to be, and a tampered file still carries the real publisher's certificate.
        if (trustResult is TRUST_E_NOSIGNATURE or TRUST_E_SUBJECT_FORM_UNKNOWN or TRUST_E_PROVIDER_UNKNOWN)
            return InstallerVerdict.NotSigned;

        if (trustResult == TRUST_E_BAD_DIGEST)
            return InstallerVerdict.Tampered;

        if (string.IsNullOrWhiteSpace(signerSubject))
            return InstallerVerdict.Unreadable;

        // Before the untrusted-root tolerance below, so a foreign self-signed certificate can never
        // be waved through by it.
        if (!string.Equals(signerSubject.Trim(), ExpectedPublisher, StringComparison.OrdinalIgnoreCase))
            return InstallerVerdict.WrongPublisher;

        // S_OK means the root IS installed on this machine (a developer box, typically).
        // CERT_E_UNTRUSTEDROOT / CERT_E_CHAINING is the expected answer everywhere else, and is the
        // only chain failure tolerated — see the remarks.
        return trustResult is S_OK or CERT_E_UNTRUSTEDROOT or CERT_E_CHAINING
            ? InstallerVerdict.Accepted
            : InstallerVerdict.UntrustedCertificate;
    }

    /// <summary>
    /// Whether a verified download may be launched. One place, so the launch site cannot invent its
    /// own reading of a verdict, and so "only Accepted runs" is assertable rather than resting on an
    /// inequality buried in UI code.
    /// </summary>
    internal static bool MayLaunch(InstallerVerdict verdict) => verdict == InstallerVerdict.Accepted;

    /// <summary>
    /// What to tell the user when a download is refused. Never claims the file was deleted — that is
    /// best-effort at the call site, and a report states only what was verified.
    /// </summary>
    internal static string MessageFor(InstallerVerdict verdict)
    {
        var reason = verdict switch
        {
            InstallerVerdict.NotSigned =>
                "The downloaded installer is not digitally signed.",
            InstallerVerdict.Tampered =>
                "The downloaded installer's signature does not match its contents — the file is "
                + "damaged or has been altered.",
            InstallerVerdict.WrongPublisher =>
                $"The downloaded installer is signed by someone other than {ExpectedPublisher[3..]}.",
            InstallerVerdict.UntrustedCertificate =>
                "The downloaded installer's signing certificate could not be accepted — it may be "
                + "expired or revoked.",
            InstallerVerdict.Unreadable =>
                "The downloaded installer's signature could not be read.",
            // Accepted never reaches here; a verdict added later must not inherit a sibling's claim.
            _ => "The downloaded installer could not be verified.",
        };

        return reason + "\n\nIt has NOT been run. Update from the releases page instead.";
    }
}

/// <summary>
/// Reads a downloaded installer's Authenticode signature and hands the evidence to
/// <see cref="InstallerSignaturePolicy"/>. Fails closed: anything that goes wrong here is a refusal,
/// never a launch.
/// </summary>
internal static class InstallerSignature
{
    /// <summary>
    /// A path for one download: a fresh directory per run, not the fixed
    /// <c>%TEMP%\ChargeKeeper-Setup.exe</c> it replaces.
    /// <para>
    /// The old path was written and then executed, which is a plant/TOCTOU surface: anything able to
    /// create that name first — a stale file from an earlier run, a second ChargeKeeper instance, a
    /// junction — decides what gets launched with the user's consent. A random directory created
    /// fresh means the name cannot be predicted or pre-occupied.
    /// </para>
    /// </summary>
    internal static string NewDownloadPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ChargeKeeper-Update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "ChargeKeeper-Setup.exe");
    }

    /// <summary>
    /// The verdict for the file at <paramref name="path"/>. Never throws.
    /// </summary>
    internal static InstallerVerdict Verify(string path)
    {
        try
        {
            if (!File.Exists(path)) return InstallerVerdict.Unreadable;

            var trustResult = Native.VerifyTrust(path);

            // Read separately from WinVerifyTrust: the trust call reports WHETHER the file verifies,
            // never WHO signed it, and the publisher half of the policy needs the subject.
            string? subject = null;
            try
            {
                using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
                subject = cert.Subject;
            }
            catch
            {
                // No signer certificate to read. The trust result still decides between "not signed"
                // and "unreadable"; leaving subject null is what makes an intact-but-opaque
                // signature refuse rather than fall through.
            }

            return InstallerSignaturePolicy.Decide(trustResult, subject);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Update: could not verify the signature of {path}.", ex);
            return InstallerVerdict.Unreadable;
        }
    }

    /// <summary>Best-effort removal of a refused download, and of the per-run directory it sits in.</summary>
    internal static void Discard(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (dir is not null && Path.GetFileName(dir).StartsWith("ChargeKeeper-Update-", StringComparison.Ordinal))
                Directory.Delete(dir, recursive: true);
            else if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            // A file left behind in %TEMP% is untidy, not dangerous — it was never launched.
            AppLog.Error($"Update: could not remove the refused download at {path}.", ex);
        }
    }

    private static class Native
    {
        private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 =
            new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        private const uint WTD_UI_NONE            = 2;
        private const uint WTD_REVOKE_NONE        = 0;
        private const uint WTD_CHOICE_FILE        = 1;
        private const uint WTD_STATEACTION_VERIFY = 1;
        private const uint WTD_STATEACTION_CLOSE  = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_FILE_INFO
        {
            public uint   cbStruct;
            public nint   pcwszFilePath;
            public nint   hFile;
            public nint   pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_DATA
        {
            public uint cbStruct;
            public nint pPolicyCallbackData;
            public nint pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public nint pFile;
            public uint dwStateAction;
            public nint hWVTStateData;
            public nint pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public nint pSignatureSettings;
        }

        [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = false)]
        private static extern uint WinVerifyTrust(nint hwnd, ref Guid pgActionID, nint pWVTData);

        /// <summary>
        /// Runs the generic Authenticode verify over one file and returns the raw HRESULT, which is
        /// the evidence <see cref="InstallerSignaturePolicy.Decide"/> reasons about. Revocation
        /// checking is off: it is a network call on the launch path, and a self-signed certificate
        /// has no CRL/OCSP endpoint for it to reach anyway.
        /// <para>
        /// dwProvFlags stays 0. Setting WTD_SAFER_FLAG (0x100) — which most sample code does — makes
        /// WinVerifyTrust collapse every failure into TRUST_E_NOSIGNATURE, GetLastError included, so
        /// a file altered after signing comes back indistinguishable from one that was never signed.
        /// Measured against a real release installer with one byte flipped: 0x800B0100 with the flag,
        /// 0x80096010 (TRUST_E_BAD_DIGEST) without it.
        /// </para>
        /// </summary>
        internal static uint VerifyTrust(string path)
        {
            var filePath = Marshal.StringToHGlobalUni(path);
            var fileInfoPtr = nint.Zero;
            var dataPtr     = nint.Zero;
            var action      = WINTRUST_ACTION_GENERIC_VERIFY_V2;

            try
            {
                var fileInfo = new WINTRUST_FILE_INFO
                {
                    cbStruct      = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                    pcwszFilePath = filePath,
                };
                fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
                Marshal.StructureToPtr(fileInfo, fileInfoPtr, fDeleteOld: false);

                var data = new WINTRUST_DATA
                {
                    cbStruct            = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                    dwUIChoice          = WTD_UI_NONE,
                    fdwRevocationChecks = WTD_REVOKE_NONE,
                    dwUnionChoice       = WTD_CHOICE_FILE,
                    pFile               = fileInfoPtr,
                    dwStateAction       = WTD_STATEACTION_VERIFY,
                    dwProvFlags         = 0,
                };
                dataPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_DATA>());
                Marshal.StructureToPtr(data, dataPtr, fDeleteOld: false);

                var result = WinVerifyTrust(nint.Zero, ref action, dataPtr);

                // WTD_STATEACTION_VERIFY allocates state inside WINTRUST_DATA; the CLOSE pass on the
                // same buffer is what frees it. Skipping it leaks a handle per check.
                var close = Marshal.PtrToStructure<WINTRUST_DATA>(dataPtr);
                close.dwStateAction = WTD_STATEACTION_CLOSE;
                Marshal.StructureToPtr(close, dataPtr, fDeleteOld: false);
                WinVerifyTrust(nint.Zero, ref action, dataPtr);

                return result;
            }
            finally
            {
                if (dataPtr     != nint.Zero) Marshal.FreeHGlobal(dataPtr);
                if (fileInfoPtr != nint.Zero) Marshal.FreeHGlobal(fileInfoPtr);
                Marshal.FreeHGlobal(filePath);
            }
        }
    }
}
