using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using ChargeKeeper.Services;

namespace ChargeKeeper.Helpers;

/// <summary>What was concluded about a downloaded installer's Authenticode signature.</summary>
internal enum InstallerVerdict
{
    /// <summary>Intact and signed by the expected publisher — the only value that may be launched.
    /// Deliberately not zero, so a default-constructed verdict cannot read as permission to run.</summary>
    Accepted = 1,

    /// <summary>No Authenticode signature at all.</summary>
    NotSigned,

    /// <summary>Signed, but the signature does not match the contents: damaged or altered.</summary>
    Tampered,

    /// <summary>Signed, intact, but by somebody else.</summary>
    WrongPublisher,

    /// <summary>Right publisher, but the certificate is expired, revoked or distrusted.</summary>
    UntrustedCertificate,

    /// <summary>The signature could not be examined at all.</summary>
    Unreadable,
}

/// <summary>
/// The launch/refuse decision for a downloaded installer, kept pure so it can be exercised without
/// a signed fixture. <see cref="InstallerSignature"/> is the half that touches files.
/// </summary>
/// <remarks>
/// Not plain chain validation: the release certificate is self-signed, so a full chain-trust check
/// fails on every machine that has not installed that root — every user's machine. Instead the
/// signature must be cryptographically intact and the signer subject exactly
/// <see cref="ExpectedPublisher"/>, and only the untrusted/incomplete root a self-signed certificate
/// necessarily produces is tolerated; expired and revoked are still refused. The residual gap is
/// that anyone can mint a self-signed certificate with the same subject. Thumbprint pinning would
/// close it, at the cost of turning the next certificate rotation into a silent update outage.
/// </remarks>
internal static class InstallerSignaturePolicy
{
    /// <summary>The subject the release certificate must carry, exactly as signtool writes it.</summary>
    internal const string ExpectedPublisher = "CN=ZeroZero Software";

    // WinVerifyTrust result codes. Anything not named here falls through to UntrustedCertificate.
    internal const uint S_OK                        = 0x00000000;
    internal const uint TRUST_E_NOSIGNATURE         = 0x800B0100;
    internal const uint TRUST_E_SUBJECT_FORM_UNKNOWN = 0x800B0003;
    internal const uint TRUST_E_PROVIDER_UNKNOWN    = 0x800B0001;
    internal const uint TRUST_E_BAD_DIGEST          = 0x80096010;
    internal const uint CERT_E_UNTRUSTEDROOT        = 0x800B0109;
    internal const uint CERT_E_CHAINING             = 0x800B010A;
    internal const uint CERT_E_EXPIRED              = 0x800B0101;

    /// <summary>The verdict for one downloaded file, from what WinVerifyTrust returned and the signer
    /// certificate's subject (null when none could be read).</summary>
    internal static InstallerVerdict Decide(uint trustResult, string? signerSubject)
    {
        // "Not signed" and "tampered" come first: both hold regardless of who the certificate claims
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

        // S_OK means the root is installed here; untrusted-root/chaining is the expected answer
        // everywhere else and the only chain failure tolerated — see the remarks.
        return trustResult is S_OK or CERT_E_UNTRUSTEDROOT or CERT_E_CHAINING
            ? InstallerVerdict.Accepted
            : InstallerVerdict.UntrustedCertificate;
    }

    /// <summary>Whether a verified download may be launched. One place, so "only Accepted runs" is
    /// assertable rather than resting on an inequality buried in UI code.</summary>
    internal static bool MayLaunch(InstallerVerdict verdict) => verdict == InstallerVerdict.Accepted;

    /// <summary>What to tell the user when a download is refused. Never claims the file was deleted —
    /// that is best-effort at the call site.</summary>
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
    /// <summary>Prefix of the per-run download directory under %TEMP%.</summary>
    private const string DownloadDirPrefix = "ChargeKeeper-Update-";

    /// <summary>
    /// A path for one download: a fresh, unpredictable directory per run. A fixed path is a
    /// plant/TOCTOU surface — anything able to create that name first decides what gets launched
    /// with the user's consent.
    /// </summary>
    internal static string NewDownloadPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), DownloadDirPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "ChargeKeeper-Setup.exe");
    }

    /// <summary>
    /// Removes download directories left over from earlier update runs. An accepted update launches
    /// the installer and exits on the next line, so <see cref="Discard"/> never runs and the ~56 MB
    /// directory is orphaned. Best-effort per directory: one that is locked must not stop the rest,
    /// and a failure here must never affect the update flow.
    /// </summary>
    public static void SweepPreviousDownloads()
    {
        try
        {
            // GetDirectories, not EnumerateDirectories: deleting entries out of a live enumeration
            // of the same parent can skip or throw.
            foreach (var dir in Directory.GetDirectories(Path.GetTempPath(), DownloadDirPrefix + "*"))
            {
                try { Directory.Delete(dir, recursive: true); }
                catch { /* still in use, or not ours to delete */ }
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Update: could not sweep previous download directories.", ex);
        }
    }

    /// <summary>The verdict for the file at <paramref name="path"/>. Never throws.</summary>
    internal static InstallerVerdict Verify(string path)
    {
        try
        {
            if (!File.Exists(path)) return InstallerVerdict.Unreadable;

            var trustResult = Native.VerifyTrust(path);

            // Read separately: WinVerifyTrust reports whether the file verifies, never who signed it.
            string? subject = null;
            try
            {
                // Not X509CertificateLoader.LoadCertificateFromFile — that loader reads a file that
                // IS a certificate, and fails with CRYPT_E_NOT_FOUND on a signed PE.
                if (Native.ReadSignerCertificate(path) is { } der)
                {
                    using var cert = X509CertificateLoader.LoadCertificate(der);
                    subject = cert.Subject;
                }
            }
            catch
            {
                // Leaving subject null is what makes an intact-but-opaque signature refuse rather
                // than fall through.
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
            if (dir is not null && Path.GetFileName(dir).StartsWith(DownloadDirPrefix, StringComparison.Ordinal))
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
        /// Runs the generic Authenticode verify over one file and returns the raw HRESULT. Revocation
        /// checking is off: it is a network call on the launch path, and a self-signed certificate has
        /// no CRL/OCSP endpoint to reach anyway.
        /// <para>
        /// dwProvFlags must stay 0. WTD_SAFER_FLAG (0x100), which most sample code sets, makes
        /// WinVerifyTrust collapse every failure into TRUST_E_NOSIGNATURE, so an altered file becomes
        /// indistinguishable from an unsigned one. Measured on a release installer with one byte
        /// flipped: 0x800B0100 with the flag, 0x80096010 (TRUST_E_BAD_DIGEST) without.
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

                // VERIFY allocates state inside WINTRUST_DATA; the CLOSE pass on the same buffer is
                // what frees it. Skipping it leaks a handle per check.
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
        /// carries no readable embedded signature. Replaces the obsoleted (SYSLIB0057)
        /// <c>X509Certificate.CreateFromSignedFile</c>: CryptQueryObject opens the PE's embedded
        /// PKCS#7, CMSG_SIGNER_CERT_INFO_PARAM names the signer, and the store lookup returns it —
        /// so the bytes are identical to what Windows itself treats as the signer.
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
