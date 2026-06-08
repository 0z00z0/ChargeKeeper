<#
.SYNOPSIS
    Builds the per-user Inno Setup installer for Lenovo Power Tray.

.DESCRIPTION
    1. Builds the native Smart Charge bridge (native\build.cmd → LenPower.dll).
    2. Publishes the app fully self-contained (win-x64, Windows App SDK bundled, no trimming —
       trimming breaks WinUI 3).
    3. Compiles installer\LenovoPowerTray.iss with Inno Setup (ISCC.exe).

    Output: installer\Output\LenovoPowerTray-Setup.exe (per-user, no admin to install;
    the app elevates itself at runtime).

    Requires Inno Setup (ISCC). If missing, install it once:
        winget install JRSoftware.InnoSetup

.EXAMPLE
    .\build-installer.ps1                  # auto-bumps patch (e.g. 1.0.2 → 1.0.3)
    .\build-installer.ps1 -Version 1.1.0   # explicit override
#>
[CmdletBinding()]
param(
    [string] $Version = ""   # empty = auto-bump patch from .csproj
)

$ErrorActionPreference = "Stop"

$installerDir = $PSScriptRoot
$root         = Split-Path $installerDir -Parent
$proj         = Join-Path $root "LenovoTray.csproj"
$publishDir   = Join-Path $root "publish"
$iss          = Join-Path $installerDir "LenovoPowerTray.iss"

# ── 0. Resolve / bump version ────────────────────────────────────────────────
$projContent = Get-Content $proj -Raw
$vMatch      = [regex]::Match($projContent, '<Version>(\d+\.\d+\.\d+)</Version>')
if (-not $vMatch.Success) { throw "Cannot find <Version>x.y.z</Version> in $proj" }
$currentVersion = $vMatch.Groups[1].Value

if ([string]::IsNullOrEmpty($Version)) {
    # Auto-bump: increment patch component
    $v       = [System.Version]$currentVersion
    $Version = "{0}.{1}.{2}" -f $v.Major, $v.Minor, ($v.Build + 1)
    Write-Host "==> Auto-bumping version:  $currentVersion  ->  $Version" -ForegroundColor Cyan
} else {
    Write-Host "==> Using explicit version: $Version" -ForegroundColor Cyan
}

# Write the new version back to the .csproj (idempotent if already correct)
if ($currentVersion -ne $Version) {
    ($projContent -replace "<Version>$currentVersion</Version>", "<Version>$Version</Version>") |
        Set-Content $proj -NoNewline
    Write-Host "    Updated LenovoTray.csproj: $currentVersion -> $Version" -ForegroundColor DarkGray
}

# ── 1. Native bridge (Smart Charge) ──────────────────────────────────────────
Write-Host "==> Building native bridge (LenPower.dll)..." -ForegroundColor Cyan
& (Join-Path $root "native\build.cmd")
if ($LASTEXITCODE -ne 0) { throw "native\build.cmd failed ($LASTEXITCODE)." }

# ── 2. Publish the app (fully self-contained, no trim) ───────────────────────
Write-Host "==> Publishing app (self-contained win-x64, Windows App SDK bundled)..." -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $proj `
    -c Release -r win-x64 --self-contained true `
    -p:WindowsAppSDKSelfContained=true -p:PublishTrimmed=false -p:PublishReadyToRun=true `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

if (-not (Test-Path (Join-Path $publishDir "LenPower.dll"))) {
    Write-Warning "LenPower.dll is not in the publish output — Smart Charge will show as Unavailable."
}

if (-not (Test-Path (Join-Path $publishDir "LenovoTray.pri"))) {
    throw "LenovoTray.pri missing from publish output — WinUI would crash at startup (0xC000027B)."
}

# ── 2b. Sign the published exe ───────────────────────────────────────────────
# dotnet publish creates a fresh apphost in the publish folder — a separate binary
# from the bin\ build output that SignOutput already signed. Sign this copy so the
# installed exe is not flagged as Unsigned by security tools.
$publishedExe = Join-Path $publishDir "LenovoTray.exe"
if (Test-Path $publishedExe) {
    Write-Host "==> Signing published exe..." -ForegroundColor Cyan
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root "scripts\sign.ps1") -Path $publishedExe
}

# ── 3. Locate Inno Setup compiler ────────────────────────────────────────────
$iscc = (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    foreach ($p in @(
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",     # winget per-user install
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe")) {
        if (Test-Path $p) { $iscc = $p; break }
    }
}
if (-not $iscc) {
    throw "Inno Setup (ISCC.exe) not found. Install it once with:  winget install JRSoftware.InnoSetup"
}

# ── 4. Compile the installer ─────────────────────────────────────────────────
Write-Host "==> Compiling installer with $iscc ..." -ForegroundColor Cyan
& $iscc "/DAppVersion=$Version" "/DPublishDir=$publishDir" $iss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)." }

$setup = Join-Path $installerDir "Output\LenovoPowerTray-Setup.exe"

# ── 5. Sign the installer exe ────────────────────────────────────────────────
# Sign before computing the SHA so the printed hash matches the distributed file.
if (Test-Path $setup) {
    Write-Host "==> Signing installer..." -ForegroundColor Cyan
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root "scripts\sign.ps1") -Path $setup
    # Non-fatal: sign.ps1 prints a warning and exits 0 if the cert is absent.
}

Write-Host ""
Write-Host "Done -> $setup" -ForegroundColor Green
if (Test-Path $setup) {
    $sha = (Get-FileHash $setup -Algorithm SHA256).Hash
    Write-Host "SHA256: $sha"
}
