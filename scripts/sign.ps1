<#
.SYNOPSIS
    Code-signs LenovoTray.exe with a self-signed certificate.

.DESCRIPTION
    Run once with -Setup to create a self-signed code-signing certificate in the
    current user's store and register it as a trusted root + trusted publisher,
    so Windows treats the signature as valid (no "Unknown Publisher" UAC banner).

    Without -Setup, the script signs the target executable using the existing
    certificate. This mode is invoked automatically by the Release build (see the
    SignOutput target in LenovoTray.csproj) and exits 0 if no certificate is found,
    so it never breaks a build.

    To use a real CA-issued certificate instead, import it into Cert:\CurrentUser\My
    with the same -Subject and skip -Setup; signing picks it up by subject name.

.EXAMPLE
    .\sign.ps1 -Setup          # one-time: create + trust the certificate
    .\sign.ps1                 # sign the latest Release build
#>
[CmdletBinding()]
param(
    [switch] $Setup,
    [string] $Path,                                        # exe to sign (defaults to Release output)
    [string] $Subject      = "CN=Zero Zero Software",
    [string] $TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"

# Returns the newest non-expired code-signing cert matching $Subject, or $null.
# Filters by the Code Signing EKU (OID 1.3.6.1.5.5.7.3.3) rather than the
# -CodeSigningCert dynamic parameter, which is unreliable under Windows PowerShell 5.1.
function Get-SigningCertificate {
    Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Subject -eq $Subject -and
            $_.NotAfter -gt (Get-Date) -and
            $_.EnhancedKeyUsageList.ObjectId -contains '1.3.6.1.5.5.7.3.3'
        } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
}

# Creates the self-signed cert and trusts it for the current user.
function New-TrustedSigningCertificate {
    Write-Host "Creating self-signed code-signing certificate '$Subject'..."
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $Subject `
        -CertStoreLocation Cert:\CurrentUser\My `
        -KeyUsage DigitalSignature `
        -KeyExportPolicy Exportable `
        -NotAfter (Get-Date).AddYears(5) `
        -FriendlyName "LenovoTray Code Signing"

    # Trust the cert for the current user so the signature validates without admin.
    $pub = Join-Path $env:TEMP "lenovotray-pub.cer"
    try {
        Export-Certificate -Cert $cert -FilePath $pub | Out-Null
        Import-Certificate -FilePath $pub -CertStoreLocation Cert:\CurrentUser\Root             | Out-Null
        Import-Certificate -FilePath $pub -CertStoreLocation Cert:\CurrentUser\TrustedPublisher | Out-Null
    }
    finally {
        Remove-Item $pub -ErrorAction SilentlyContinue
    }

    Write-Host "Certificate created and trusted (thumbprint $($cert.Thumbprint))."
    return $cert
}

# ── Setup mode ──────────────────────────────────────────────────────────────
if ($Setup) {
    if (Get-SigningCertificate) {
        Write-Host "A signing certificate for '$Subject' already exists. Nothing to do."
    }
    else {
        New-TrustedSigningCertificate | Out-Null
    }
    return
}

# ── Sign mode ───────────────────────────────────────────────────────────────

# Default to the Release apphost when no path is supplied.
if (-not $Path) {
    $Path = Join-Path $PSScriptRoot "bin\Release\net10.0-windows10.0.26100.0\win-x64\LenovoTray.exe"
}

if (-not (Test-Path $Path)) {
    Write-Warning "Nothing to sign: '$Path' does not exist."
    return
}

$cert = Get-SigningCertificate
if (-not $cert) {
    # Don't fail the build — just inform the developer how to enable signing.
    Write-Warning "No signing certificate for '$Subject'. Run '.\sign.ps1 -Setup' first. Skipping."
    return
}

Write-Host "Signing $Path ..."
$result = Set-AuthenticodeSignature `
    -FilePath $Path `
    -Certificate $cert `
    -HashAlgorithm SHA256 `
    -TimestampServer $TimestampUrl

if ($result.Status -ne "Valid") {
    throw "Signing failed: $($result.Status) - $($result.StatusMessage)"
}
Write-Host "Signed successfully (status: $($result.Status))."
