<#
.SYNOPSIS
    Code-signs ChargeKeeper.exe with a self-signed certificate.

.DESCRIPTION
    Run once with -Setup to create a self-signed code-signing certificate in the
    current user's store and register it as a trusted root + trusted publisher,
    so Windows treats the signature as valid (no "Unknown Publisher" UAC banner).

    Without -Setup, the script signs the target executable using the existing
    certificate. This mode is invoked automatically by the Release build (see the
    SignOutput target in ChargeKeeper.csproj) and exits 0 if no certificate is found,
    so it never breaks a build.

    The signing itself belongs to the build kit's Sign-Executable.ps1, which ships in
    the ZeroZero.Build package. It reads the file back off disk and refuses a signature that
    arrived without a timestamp (ZZS013) — an outcome Set-AuthenticodeSignature reports
    as Valid, and one that stops verifying the day the certificate expires.

    -NoTimestamp signs without asking a timestamp server. A local build passes it, following the
    ZeroZeroSignNoTimestamp property in ChargeKeeper.csproj; a GitHub Actions build does not.

    To use a real CA-issued certificate instead, import it into Cert:\CurrentUser\My
    with the same -Subject and skip -Setup; signing picks it up by subject name.

    NOTE: The same certificate (CN=ZeroZero Software) is shared with the sibling
    HyperVManagerTray project. Running -Setup only once (on either project) is sufficient.

.EXAMPLE
    .\sign.ps1 -Setup          # one-time: create + trust the certificate
    .\sign.ps1                 # sign the latest Release build
#>
[CmdletBinding()]
param(
    [switch] $Setup,
    [string] $Path,                                        # exe to sign (defaults to Release output)
    [string] $Subject      = "CN=ZeroZero Software",
    [string] $TimestampUrl = "http://timestamp.digicert.com",
    # The build kit's signing script. The Release build passes the path the kit itself publishes;
    # a hand run resolves it from the restored package. Resolved in the body, not here:
    # $PSScriptRoot is still empty while a parameter default is evaluated under Windows PowerShell.
    [string] $Signer,
    # Signs without a timestamp. Passed by local builds only; see ZeroZeroSignNoTimestamp.
    [switch] $NoTimestamp
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
        -FriendlyName "ChargeKeeper Code Signing"

    # Trust the cert for the current user so the signature validates without admin.
    $pub = Join-Path $env:TEMP "chargekeeper-pub.cer"
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
# This script lives in scripts\, so the project root is one level up.
if (-not $Path) {
    $repoRoot = Split-Path $PSScriptRoot -Parent
    $Path = Join-Path $repoRoot "bin\Release\net10.0-windows10.0.26100.0\win-x64\ChargeKeeper.exe"
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

# The kit's signer inside the restored SDK package. The Release build hands the path over as
# -Signer; a hand run finds it from the version global.json names, in the package folder NuGet uses.
if (-not $Signer) {
    $repo    = Split-Path $PSScriptRoot -Parent
    $version = (Get-Content (Join-Path $repo 'global.json') -Raw |
                ConvertFrom-Json).'msbuild-sdks'.'ZeroZero.Build'
    $packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES }
                else { Join-Path $env:USERPROFILE '.nuget\packages' }
    $Signer = Join-Path $packages "zerozero.build\$version\scripts\Sign-Executable.ps1"
}

if (-not (Test-Path $Signer)) {
    # Nothing is restored in a docs-only checkout; signing is a build-time nicety, not a gate.
    Write-Warning "The build kit's signer is not at '$Signer'. Skipping."
    return
}

$signerArguments = @('-Path', $Path, '-Thumbprint', $cert.Thumbprint)
if ($NoTimestamp) { $signerArguments += '-NoTimestamp' }
else              { $signerArguments += @('-TimestampServer', $TimestampUrl) }

Write-Host "Signing $Path ..."
& powershell -NoProfile -ExecutionPolicy Bypass -File $Signer @signerArguments
if ($LASTEXITCODE -ne 0) {
    throw "Signing failed: the build kit's signer exited $LASTEXITCODE."
}
