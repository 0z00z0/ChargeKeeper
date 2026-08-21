using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
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

    /// <summary>Signed, intact, and the subject reads as the expected publisher, but the certificate
    /// is not one this build pins. Either an impersonation — a subject is trivially forged on a
    /// self-signed certificate, a thumbprint is not — or a certificate rotation this build predates.
    /// Distinct from <see cref="WrongPublisher"/> so the two are separable in a log: a foreign
    /// publisher is somebody else's file, an unpinned certificate is a claim to be ours.</summary>
    UnpinnedCertificate,
}

/// <summary>
/// The launch/refuse decision for a downloaded installer, kept pure so it can be exercised without
/// a signed fixture: it takes the evidence — what WinVerifyTrust concluded, and which certificate
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
/// what catches a tampered or swapped file), the signer subject must be exactly
/// <see cref="ExpectedPublisher"/>, and the signer's SHA-256 thumbprint must be one of
/// <see cref="PinnedSigningThumbprints"/>. Beyond that, one and only one chain failure is
/// tolerated — the untrusted/incomplete root that the self-signed certificate necessarily
/// produces. An expired or revoked certificate is still refused.
/// </para>
/// <para>
/// The subject check alone left a gap, and the pin is what closes it: because an untrusted root is
/// tolerated, anyone could mint a self-signed certificate whose subject reads
/// <c>CN=ZeroZero Software</c> and satisfy a name comparison. A subject is a free-text field on a
/// self-signed certificate; a SHA-256 thumbprint is the hash of the certificate itself. So the
/// signer's thumbprint must also appear in <see cref="PinnedSigningThumbprints"/>, and it is that
/// pin — not the name — that makes tolerating an untrusted root safe.
/// </para>
/// <para>
/// The cost is that certificate rotation becomes a compatibility event rather than a build detail;
/// <see cref="PinnedSigningThumbprints"/> states what rotating one requires.
/// </para>
/// </remarks>
internal static class InstallerSignaturePolicy
{
    /// <summary>The subject the release certificate must carry, exactly as signtool writes it.</summary>
    internal const string ExpectedPublisher = "CN=ZeroZero Software";

    /// <summary>
    /// SHA-256 thumbprints of every signing certificate this build accepts. A collection, not a
    /// single value, because rotation requires two to be valid at once.
    /// <para>
    /// ROTATING THE SIGNING CERTIFICATE: a build only trusts the thumbprints listed here, so a
    /// certificate that is not in a user's installed build cannot deliver the update that would add
    /// it. The new thumbprint must therefore SHIP IN A RELEASED BUILD BEFORE ANYTHING IS SIGNED WITH
    /// IT — add it here alongside the old one, release and let that release propagate, and only then
    /// start signing with the new certificate. Both entries stay until the old build is out of use;
    /// removing the outgoing thumbprint too early strands every machine still on it, silently, with
    /// the update refused rather than any signal that a rotation happened.
    /// </para>
    /// <para>
    /// SHA-256, not SHA-1: <c>X509Certificate2.Thumbprint</c> and the certificate dialog's
    /// "Thumbprint" field are SHA-1, which is why the two are easy to confuse. The SHA-1 thumbprint
    /// of the current certificate is <c>4909D644147756958E31783CF9D5926873522197</c>; it is recorded
    /// here only so it is not mistaken for the pin. Entries below are 64 hex characters.
    /// </para>
    /// </summary>
    internal static readonly IReadOnlyList<string> PinnedSigningThumbprints =
    [
        // ZeroZero Software, self-signed, serial 18B07761756164B3445E91436D4EA284,
        // valid 2026-06-08 to 2031-06-08. Read from the signed ChargeKeeper-Setup-1.10.0.exe.
        "486E2A37273DFE6584655C29B042E7F1A5468DA10E3BB3CC4B952E51570757F4",
    ];

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
    /// Whether <paramref name="thumbprint"/> is one of <see cref="PinnedSigningThumbprints"/>.
    /// Compared on hex digits alone, so a value carrying the colons or spaces that certificate tools
    /// print, or written in either case, still matches. An empty or unreadable thumbprint is not a
    /// match — the pin fails closed.
    /// </summary>
    internal static bool IsPinnedThumbprint(string? thumbprint)
    {
        var candidate = NormaliseThumbprint(thumbprint);
        if (candidate.Length == 0) return false;

        // The collection is walked rather than a single value compared, so adding a rollover
        // certificate is one more entry in the list and nothing else.
        foreach (var pinned in PinnedSigningThumbprints)
        {
            if (string.Equals(candidate, NormaliseThumbprint(pinned), StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>Hex digits only, upper case. Anything else in the input is dropped.</summary>
    private static string NormaliseThumbprint(string? thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint)) return string.Empty;

        var buffer = new StringBuilder(thumbprint.Length);
        foreach (var c in thumbprint)
        {
            if (c is >= '0' and <= '9') buffer.Append(c);
            else if (c is >= 'A' and <= 'F') buffer.Append(c);
            else if (c is >= 'a' and <= 'f') buffer.Append(char.ToUpperInvariant(c));
        }

        return buffer.ToString();
    }

    /// <summary>
    /// The verdict for one downloaded file.
    /// </summary>
    /// <param name="trustResult">What WinVerifyTrust returned for the file.</param>
    /// <param name="signerSubject">The signer certificate's subject, or null when none could be read.</param>
    /// <param name="signerThumbprint">The signer certificate's SHA-256 thumbprint, or null when none
    /// could be read. SHA-256 specifically — a SHA-1 value can never match a pin.</param>
    internal static InstallerVerdict Decide(uint trustResult, string? signerSubject, string? signerThumbprint)
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

        // Subject first, and only so the verdict is informative: a file signed by a real, unrelated
        // publisher reads as WrongPublisher rather than as a failed pin. It decides nothing on its
        // own — the thumbprint below is the check that has to hold.
        if (!string.Equals(signerSubject.Trim(), ExpectedPublisher, StringComparison.OrdinalIgnoreCase))
            return InstallerVerdict.WrongPublisher;

        if (string.IsNullOrWhiteSpace(signerThumbprint))
            return InstallerVerdict.Unreadable;

        // The check that makes the untrusted-root tolerance below safe. Without it, minting a
        // self-signed certificate named CN=ZeroZero Software would be enough to pass.
        if (!IsPinnedThumbprint(signerThumbprint))
            return InstallerVerdict.UnpinnedCertificate;

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
            InstallerVerdict.UnpinnedCertificate =>
                "The downloaded installer is signed with a certificate this version of ChargeKeeper "
                + "does not recognise.",
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
            // never WHO signed it, and the identity half of the policy needs both the subject and
            // the certificate's own SHA-256 hash.
            string? subject    = null;
            string? thumbprint = null;
            try
            {
                // The DER blob comes from crypt32, not X509CertificateLoader.LoadCertificateFromFile:
                // that loader reads a file that IS a certificate, and an Authenticode-signed PE is not
                // one — it fails with CRYPT_E_NOT_FOUND on a real signed installer.
                if (Native.ReadSignerCertificate(path) is { } der)
                {
                    using var cert = X509CertificateLoader.LoadCertificate(der);
                    subject = cert.Subject;
                    // NOT cert.Thumbprint, which is SHA-1. The pin is SHA-256.
                    thumbprint = cert.GetCertHashString(HashAlgorithmName.SHA256);
                }
            }
            catch
            {
                // No signer certificate to read. The trust result still decides between "not signed"
                // and "unreadable"; leaving both null is what makes an intact-but-opaque signature
                // refuse rather than fall through.
            }

            return InstallerSignaturePolicy.Decide(trustResult, subject, thumbprint);
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

        private const uint CERT_QUERY_OBJECT_FILE                    = 0x00000001;
        private const uint CERT_QUERY_CONTENT_FLAG_PKCS7_SIGNED_EMBED = 1 << 10;
        private const uint CERT_QUERY_FORMAT_FLAG_BINARY             = 1 << 1;
        private const uint CMSG_SIGNER_CERT_INFO_PARAM               = 7;
        private const uint X509_ASN_ENCODING                         = 0x00000001;
        private const uint PKCS_7_ASN_ENCODING                       = 0x00010000;

        [StructLayout(LayoutKind.Sequential)]
        private struct CERT_CONTEXT
        {
            public uint dwCertEncodingType;
            public nint pbCertEncoded;
            public uint cbCertEncoded;
            public nint pCertInfo;
            public nint hCertStore;
        }

        [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptQueryObject(
            uint dwObjectType, [MarshalAs(UnmanagedType.LPWStr)] string pvObject,
            uint dwExpectedContentTypeFlags, uint dwExpectedFormatTypeFlags, uint dwFlags,
            out uint pdwMsgAndCertEncodingType, out uint pdwContentType, out uint pdwFormatType,
            out nint phCertStore, out nint phMsg, out nint ppvContext);

        [DllImport("crypt32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptMsgGetParam(
            nint hCryptMsg, uint dwParamType, uint dwIndex, nint pvData, ref uint pcbData);

        [DllImport("crypt32.dll", SetLastError = true)]
        private static extern nint CertGetSubjectCertificateFromStore(
            nint hCertStore, uint dwCertEncodingType, nint pCertInfo);

        [DllImport("crypt32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CertFreeCertificateContext(nint pCertContext);

        [DllImport("crypt32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CertCloseStore(nint hCertStore, uint dwFlags);

        [DllImport("crypt32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptMsgClose(nint hCryptMsg);

        /// <summary>
        /// DER bytes of the certificate that signed <paramref name="path"/>, or null when the file
        /// carries no readable embedded signature.
        /// <para>
        /// This is the path <c>X509Certificate.CreateFromSignedFile</c> took before it was obsoleted
        /// (SYSLIB0057): CryptQueryObject opens the PE's embedded PKCS#7 through the OS subject
        /// interface package, CMSG_SIGNER_CERT_INFO_PARAM names the signer among the certificates the
        /// message carries, and the store lookup returns that one. Reading it here rather than
        /// letting a managed loader parse the PE keeps the bytes identical to what Windows itself
        /// treats as the signer — the pin compares a hash of exactly these bytes.
        /// </para>
        /// </summary>
        internal static byte[]? ReadSignerCertificate(string path)
        {
            var hStore     = nint.Zero;
            var hMsg       = nint.Zero;
            var certInfo   = nint.Zero;
            var pCertContext = nint.Zero;

            try
            {
                if (!CryptQueryObject(CERT_QUERY_OBJECT_FILE, path,
                                      CERT_QUERY_CONTENT_FLAG_PKCS7_SIGNED_EMBED,
                                      CERT_QUERY_FORMAT_FLAG_BINARY, 0,
                                      out _, out _, out _, out hStore, out hMsg, out _))
                    return null;

                uint size = 0;
                if (!CryptMsgGetParam(hMsg, CMSG_SIGNER_CERT_INFO_PARAM, 0, nint.Zero, ref size) || size == 0)
                    return null;

                certInfo = Marshal.AllocHGlobal((int)size);
                if (!CryptMsgGetParam(hMsg, CMSG_SIGNER_CERT_INFO_PARAM, 0, certInfo, ref size))
                    return null;

                pCertContext = CertGetSubjectCertificateFromStore(
                    hStore, X509_ASN_ENCODING | PKCS_7_ASN_ENCODING, certInfo);
                if (pCertContext == nint.Zero)
                    return null;

                var context = Marshal.PtrToStructure<CERT_CONTEXT>(pCertContext);
                if (context.pbCertEncoded == nint.Zero || context.cbCertEncoded == 0)
                    return null;

                var der = new byte[context.cbCertEncoded];
                Marshal.Copy(context.pbCertEncoded, der, 0, der.Length);
                return der;
            }
            finally
            {
                if (pCertContext != nint.Zero) CertFreeCertificateContext(pCertContext);
                if (certInfo     != nint.Zero) Marshal.FreeHGlobal(certInfo);
                if (hMsg         != nint.Zero) CryptMsgClose(hMsg);
                if (hStore       != nint.Zero) CertCloseStore(hStore, 0);
            }
        }
    }
}
