<#
.SYNOPSIS
    Generates Assets\AppIcon.ico (and Assets\AppIcon.png) — the ChargeKeeper "0z0 steel battery" app icon.

.DESCRIPTION
    Draws the icon natively at each size (256/128/64/48/32/16) with System.Drawing —
    no downscaling, so small frames stay crisp — and assembles a PNG-in-ICO file
    (6-byte header + 16-byte directory entries + PNG frames; a width/height byte of 0
    means 256). PNG-compressed frames are supported by Windows Vista and later.

    A single 256x256 transparent PNG (Assets\AppIcon.png) is also emitted alongside the
    .ico as a general-purpose brand export. The app does not ship or reference it: every
    in-app surface draws the mark natively through Helpers\BrandMarkImage.

    The glyph itself — geometry and palettes — comes from scripts\BatteryGlyph.ps1, shared with
    installer\make-wizard-images.ps1; see that file for the list of representations that must stay
    in sync. It matches brand\chargekeeper-icon.svg (the authoritative vector), expressed on a
    256-unit reference canvas and scaled per frame. Flat "0z0 geometric" style: a squared body+cap,
    an interior charge fill, and a guard line (no gradients). Stroke widths are clamped so the
    battery outline and the guard line stay legible at 16 px. This script owns only what is
    specific to an ICO: the per-frame plan, the plate, and the file assembly.

    Two outputs, because the two files are drawn on different surfaces:

      (default)       Assets\AppIcon.ico (+ Assets\AppIcon.png) — the APP's own icon. Every frame
                      is ChargeKeeper's GaugePalette (SteelBlue / Sage / Terracotta) on a fully
                      transparent background, no plate. These light tones read on the DARK chrome
                      the app actually lives on: its own #0a0f17 title bar, the taskbar, Alt-Tab.

      -HighContrast   Assets\SetupIcon.ico — Inno's SetupIconFile, i.e. SETUP.EXE'S OWN ICON.
                      Rendered PER FRAME SIZE (see below), because this one file is drawn on two
                      opposite surfaces.

    Why -HighContrast uses the ink glyph with a halo, and no plate
    ---------------------------------------------------------------
    SetupIconFile is not just the wizard's title-bar icon — it is the icon of Setup.exe as a file.
    That means it is drawn on backgrounds that pull in opposite directions:

      * Inno's wizard title bar, on LIGHT chrome (#F3F3F3) — at whatever frame size the title bar's
        own DPI scaling asks for, not only 16 px: Windows requests a larger frame once display
        scaling passes 100 %, which is the common case on a laptop panel.
      * Explorer / desktop / taskbar, usually on DARK chrome (#202020 on Win11 dark mode, the
        common default).

    A single flat palette cannot serve both: measured against #202020, the dense "ink" tones score
    1.24:1 (body/cap #1C333F — effectively invisible), 2.61:1 (sage) and 2.96:1 (terra); against
    #F3F3F3 the same ink is excellent at 11.87:1. An earlier revision split the difference by frame
    size instead — ink, transparent, at 16 px; a dark #0e1620 plate with the light product glyph at
    32 px and up — on the assumption that only Explorer ever asks for the larger frames. That
    assumption is false: a title bar under DPI scaling asks for one of those larger frames too, so
    the plate showed up as a dark square sitting in Inno's light title bar.

    The fix removes the plate rather than re-drawing the size boundary: every frame is the ink
    glyph on a transparent background, with a HALO — a wider, near-white copy of the body stroke,
    the cap and the guard line drawn underneath (see BatteryGlyph.ps1's Draw-BatteryGlyph). Against
    light chrome the halo is close enough to the background to disappear, so the icon reads exactly
    as the plain ink glyph did. Against dark chrome the halo is what the eye follows — a light ring
    around a dark shape — so the icon still reads as a battery even where the ink fill itself has
    little contrast of its own. Nothing about the icon's SIZE decides its background any more,
    which is the property a DPI-scaled title bar broke.

    The frames are each rendered natively at their own size — a per-size render, not a downscale of
    one master image.

    After writing, the ICO is round-tripped through System.Drawing.Icon at several
    sizes as a sanity check that the file parses.

.EXAMPLE
    .\scripts\make-appicon.ps1                    # writes Assets\AppIcon.ico + Assets\AppIcon.png
    .\scripts\make-appicon.ps1 -OutPath my.ico    # writes elsewhere
    .\scripts\make-appicon.ps1 -HighContrast      # writes Assets\SetupIcon.ico (ink + halo, no plate)
#>
[CmdletBinding()]
param(
    [string] $OutPath = "",  # default: <repo>\Assets\AppIcon.ico (or SetupIcon.ico with -HighContrast)
    # -HighContrast: render the SetupIcon variant — the dense "ink" glyph with a halo outline, on a
    # transparent background at every frame size. See the .DESCRIPTION block for why.
    [switch] $HighContrast
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

# Glyph geometry + the Product/Ink palettes, shared with installer\make-wizard-images.ps1.
. (Join-Path $PSScriptRoot "BatteryGlyph.ps1")

$root = Split-Path $PSScriptRoot -Parent
if (-not $OutPath) {
    $OutPath = Join-Path $root ($HighContrast ? "Assets\SetupIcon.ico" : "Assets\AppIcon.ico")
}

$sizes = 256, 128, 64, 48, 32, 16

# ── Halo ──────────────────────────────────────────────────────────────────────
# SetupIcon's own treatment: near-white, mostly invisible against Inno's light title bar, a defining
# ring against dark Explorer chrome. Fixed pixel width rather than scaled by frame size — the same
# reasoning as the pen floors below, and what keeps a thin edge from vanishing at 16 px or ballooning
# at 256 px. Not the app's own AppIcon.ico, which stays plain product-palette on transparent — the
# app only ever shows it on its own dark chrome.
$haloColor = [System.Drawing.Color]::FromArgb(0xf5, 0xf7, 0xfa)
$haloWidth = 1.4

# Which treatment a given frame gets. The default (app) icon is uniform — transparent product
# palette at every size, no halo (dark chrome only). -HighContrast is uniform too, in its own
# way — the ink glyph with the halo above, at every size, so no frame depends on where it ends up
# being shown.
function Get-FramePlan([int]$size) {
    if ($HighContrast) {
        return @{ Palette = $BatteryGlyphPalettes.Ink; Halo = $true }
    }
    return @{ Palette = $BatteryGlyphPalettes.Product; Halo = $false }
}

# Renders one frame and returns it as a PNG byte array. Palette and halo are decided per frame by
# Get-FramePlan.
function New-IconFramePng([int]$size) {
    $plan = Get-FramePlan $size

    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        try {
            $g.SmoothingMode   = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $g.Clear([System.Drawing.Color]::Transparent)

            [float]$s = $size / 256.0

            # Flat "0z0 geometric" battery glyph scaled to fill the canvas (offset 0,0 — the glyph
            # IS the icon here, unlike the wizard banners which place it on a larger composition).
            # The stroke floors are what make the 16 px frame legible: below ~31 px the body
            # outline and the guard line would otherwise render sub-pixel and wash out.
            if ($plan.Halo) {
                Draw-BatteryGlyph $g 0 0 $s $plan.Palette -MinBodyPen 1.6 -MinGuardPen 2.0 `
                                  -HaloColor $haloColor -HaloWidth $haloWidth
            } else {
                Draw-BatteryGlyph $g 0 0 $s $plan.Palette -MinBodyPen 1.6 -MinGuardPen 2.0
            }
        } finally { $g.Dispose() }

        $ms = New-Object System.IO.MemoryStream
        try {
            $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
            return $ms.ToArray()
        } finally { $ms.Dispose() }
    } finally { $bmp.Dispose() }
}

# ── Render all frames ─────────────────────────────────────────────────────────
# Strongly typed as byte[]: BinaryWriter.Write() overload resolution on a loose Object[]
# silently picks a single-byte overload and corrupts the file.
Write-Host "==> Rendering frames: $($sizes -join ', ') px" -ForegroundColor Cyan
$frames = [System.Collections.Generic.List[byte[]]]::new()
foreach ($size in $sizes) {
    $plan = Get-FramePlan $size
    $treatment = if ($plan.Halo) { "transparent (ink palette + halo)" } else { "transparent" }
    Write-Host ("    {0,3}x{0,-3} {1}" -f $size, $treatment)
    $frames.Add([byte[]](New-IconFramePng $size))
}

# ── Assemble the ICO (same layout as IconGenerator.SaveAsIco) ─────────────────
$outDir = Split-Path $OutPath -Parent
if ($outDir -and -not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

$fs = [System.IO.File]::Create($OutPath)
try {
    $bw = New-Object System.IO.BinaryWriter($fs)
    try {
        # ICO file header (6 bytes)
        $bw.Write([int16]0)              # reserved — must be 0
        $bw.Write([int16]1)              # type: 1 = icon
        $bw.Write([int16]$sizes.Count)   # number of images

        # Directory entries (16 bytes each); image data starts after header + directory.
        $dataOffset = 6 + $sizes.Count * 16
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }   # 0 means 256
            $bw.Write([byte]$dim)                # width
            $bw.Write([byte]$dim)                # height
            $bw.Write([byte]0)                   # colour count (0 = true colour)
            $bw.Write([byte]0)                   # reserved
            $bw.Write([int16]1)                  # colour planes
            $bw.Write([int16]32)                 # bits per pixel
            $bw.Write([int32]$frames[$i].Length) # data size in bytes
            $bw.Write([int32]$dataOffset)        # data offset from start of file
            $dataOffset += $frames[$i].Length
        }

        # Image data
        foreach ($frame in $frames) { $bw.Write($frame) }
    } finally { $bw.Dispose() }
} finally { $fs.Dispose() }

$bytes = (Get-Item $OutPath).Length
Write-Host "==> Wrote $OutPath ($bytes bytes, $($sizes.Count) frames)" -ForegroundColor Green

# ── Verify: the file must parse as an icon at several sizes ───────────────────
foreach ($check in 256, 48, 16) {
    $icon = New-Object System.Drawing.Icon($OutPath, $check, $check)
    try {
        Write-Host ("    verify {0}x{0} -> loaded {1}x{2}" -f $check, $icon.Width, $icon.Height)
    } finally { $icon.Dispose() }
}
Write-Host "==> ICO verified OK." -ForegroundColor Green

# ── Also emit a 256x256 transparent PNG as a brand export ─────────────────────
# Nothing in the app consumes this — ChargeKeeper.csproj keeps it out of Content — but a plain PNG
# of the mark is what other surfaces ask for. Same 256-unit geometry, single frame. Skipped with
# -HighContrast (that run
# only produces the dense SetupIcon.ico and must not overwrite the app's product-palette PNG).
if (-not $HighContrast) {
    $pngPath = Join-Path (Split-Path $OutPath -Parent) "AppIcon.png"
    [System.IO.File]::WriteAllBytes($pngPath, [byte[]](New-IconFramePng 256))
    $pngBytes = (Get-Item $pngPath).Length
    Write-Host "==> Wrote $pngPath ($pngBytes bytes, 256x256)" -ForegroundColor Green
}
