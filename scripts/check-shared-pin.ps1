<#
    Warns when the live sibling 0z0-shared checkout differs from the commit pinned in
    .github/0z0-shared-ref.

    Why this exists: a local build compiles against the LIVE sibling folder while CI clones the
    PINNED commit, so a shared type that is present locally but not in the pin resolves here and
    fails CI with CS0234 "type does not exist in namespace ZeroZero.Brand.WinUI".

    Contract: prints one line when the two differ, nothing when they match, and ALWAYS exits 0.
    git or the sibling clone may legitimately be absent, and a missing prerequisite must never
    break the build. The caller raises the MSBuild warning from stdout.
#>
[CmdletBinding()]
param(
    # .github/0z0-shared-ref — the single source of truth both CI workflows read.
    [Parameter(Mandatory)] [string] $RefFile,
    # The sibling 0z0-shared working copy, i.e. ..\0z0-shared
    [Parameter(Mandatory)] [string] $SharedPath
)

$ErrorActionPreference = 'Stop'

# The body lives in a function so that every early exit still falls through to the explicit
# `exit 0` below. Without it, `powershell -File` propagates $LASTEXITCODE from the last native
# command — and `git rev-parse --verify --quiet` exits 1 whenever the pin does not resolve,
# which is exactly the mismatch case. MSBuild would then log a spurious Exec failure on top of
# the warning that is wanted.
function Get-PinMismatchMessage {
    if (-not (Test-Path -LiteralPath $RefFile)) { return }
    if (-not (Test-Path -LiteralPath (Join-Path $SharedPath '.git'))) { return }

    # Same parse as ci.yml / release.yml: first non-blank, non-comment line wins.
    $pinned = Get-Content -LiteralPath $RefFile |
              Where-Object { $_.Trim() -and $_ -notmatch '^\s*#' } |
              Select-Object -First 1
    if (-not $pinned) { return }
    $pinned = $pinned.Trim()

    $head = & git -C $SharedPath rev-parse HEAD 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $head) { return }
    $head = $head.Trim()

    # Resolve the pin through git rather than comparing strings, so a tag pin compares by the
    # commit it points at instead of always looking like a mismatch.
    $resolved = & git -C $SharedPath rev-parse --verify --quiet "$pinned^{commit}" 2>$null
    if ($LASTEXITCODE -eq 0 -and $resolved) {
        if ($resolved.Trim() -eq $head) { return }
    }
    elseif ($head.StartsWith($pinned, [StringComparison]::OrdinalIgnoreCase)) {
        # Pin is an abbreviated SHA that git could not resolve (e.g. the commit is not
        # fetched); a prefix match on HEAD is still conclusive enough to stay quiet.
        return
    }

    $short = if ($head.Length -ge 12) { $head.Substring(0, 12) } else { $head }
    "0z0-shared pin mismatch: .github/0z0-shared-ref pins '$pinned' but the sibling checkout " +
    "at $SharedPath is at $short. Local builds use the LIVE sibling; CI uses the PIN. Adopting " +
    "a new shared type means bumping the pin in the SAME change, or CI fails with CS0234."
}

try { Get-PinMismatchMessage }
catch { }   # Diagnostics must never break a build. Stay silent.

exit 0
