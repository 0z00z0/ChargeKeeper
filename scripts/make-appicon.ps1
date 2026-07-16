<#
.SYNOPSIS
    Generates Assets\AppIcon.ico (and Assets\AppIcon.png) — the ChargeKeeper "0z0 steel battery" app icon.

.DESCRIPTION
    Draws the icon natively at each size (256/128/64/48/32/16) with System.Drawing —
    no downscaling, so small frames stay crisp — and assembles a PNG-in-ICO file
    (6-byte header + 16-byte directory entries + PNG frames; a width/height byte of 0
    means 256). PNG-compressed frames are supported by Windows Vista and later.

    A single 256x256 transparent PNG (Assets\AppIcon.png) is also emitted alongside the
    .ico for in-app use (the Settings pane-footer image references ms-appx:///Assets/AppIcon.png).

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

    Why -HighContrast branches per frame size
    -----------------------------------------
    SetupIconFile is not just the wizard's title-bar icon — it is the icon of Setup.exe as a file.
    That means it is drawn on two backgrounds that pull in opposite directions:

      * Inno's wizard title bar, at 16 px, on LIGHT chrome (#F3F3F3).
      * Explorer / desktop / taskbar, at 32/48/256 px, usually on DARK chrome (#202020 on Win11
        dark mode, which is the common default).

    A single palette cannot serve both. Measured against #202020, the dense "ink" tones score
    1.24:1 (body/cap #1C333F — effectively invisible), 2.61:1 (sage) and 2.96:1 (terra); against
    #F3F3F3 the same ink is excellent at 11.87:1. The converse also holds: an earlier revision
    plated the glyph on a dark #0e1620 rounded square with the light product palette, which scores
    6.36:1 on dark Explorer but reads as an ugly dark BOX sitting in Inno's light title bar.

    So the frames split by the size each surface actually requests:

      16 px          -> dense "ink" tones, transparent, NO plate. This is the size Inno's light
                        wizard title bar asks for, and ink on light is what works there.
      32/48/64/      -> the plated treatment: dark #0e1620 rounded plate with a #1a2840 edge, the
      128/256 px        light product palette glyph on top. These are the sizes Explorer, the
                        desktop and the taskbar ask for, where the background is usually dark.

    Accepted cost, stated plainly: Explorer's "Small icons" list view can request the 16 px frame,
    and there the ink glyph will be weak against a dark background (the same 1.24:1 as above). That
    is the price of the split. It is the right trade: the 16 px frame is guaranteed to be shown on
    LIGHT chrome by the installer wizard on every run, whereas 16 px on dark Explorer is one
    optional view mode among several, and the 32 px+ frames that dark Explorer normally uses are
    the plated ones. Serving the guaranteed case correctly beats hedging both badly.

    The frames are each rendered natively at their own size — this is a per-size branch, not a
    downscale of one master image.

    After writing, the ICO is round-tripped through System.Drawing.Icon at several
    sizes as a sanity check that the file parses.

.EXAMPLE
    .\scripts\make-appicon.ps1                    # writes Assets\AppIcon.ico + Assets\AppIcon.png
    .\scripts\make-appicon.ps1 -OutPath my.ico    # writes elsewhere
    .\scripts\make-appicon.ps1 -HighContrast      # writes Assets\SetupIcon.ico (dense, for light chrome)
#>
[CmdletBinding()]
param(
    [string] $OutPath = "",  # default: <repo>\Assets\AppIcon.ico (or SetupIcon.ico with -HighContrast)
    # -HighContrast: render the SetupIcon variant, which branches PER FRAME SIZE — 16 px in dense
    # "ink" tones on transparent (Inno's LIGHT wizard title bar), 32 px and up plated on a dark
    # rounded square (DARK Explorer / desktop / taskbar). See the .DESCRIPTION block for why.
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

# ── Plate ─────────────────────────────────────────────────────────────────────
# The glyph palettes (Product / Ink) come from BatteryGlyph.ps1. The plate is this script's own —
# no other surface renders it.
#
# Plate (SetupIcon's 32 px+ frames only): a dark rounded square that guarantees the light product
# glyph a dark backdrop regardless of what Explorer paints behind the icon.
$plateFill = [System.Drawing.Color]::FromArgb(0x0e, 0x16, 0x20)
$plateEdge = [System.Drawing.Color]::FromArgb(0x1a, 0x28, 0x40)

# Which treatment a given frame gets. The default (app) icon is uniform — transparent product
# palette at every size. Only -HighContrast branches, and it branches on the size the consuming
# surface requests: 16 px is Inno's light wizard title bar (ink, no plate); 32 px and up are
# Explorer / desktop / taskbar, usually dark (plated product glyph).
function Get-FramePlan([int]$size) {
    if ($HighContrast -and $size -gt 16) {
        return @{ Palette = $BatteryGlyphPalettes.Product; Plated = $true }
    }
    if ($HighContrast) {
        return @{ Palette = $BatteryGlyphPalettes.Ink; Plated = $false }
    }
    return @{ Palette = $BatteryGlyphPalettes.Product; Plated = $false }
}

# Renders one frame and returns it as a PNG byte array. Palette and plating are decided per frame
# by Get-FramePlan, because the SetupIcon variant must serve a light 16 px title bar and dark
# 32 px+ Explorer chrome from the same file.
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

            # Dark plate behind the glyph (SetupIcon's 32 px+ frames): rounded square inset from the
            # canvas, ~12 % corner radius, #0e1620 fill with a faint #1a2840 edge. Gives the light
            # product glyph a dark backdrop on Explorer regardless of the user's theme.
            if ($plan.Plated) {
                $platePath = New-RoundedRectPath (10 * $s) (10 * $s) (236 * $s) (236 * $s) (28 * $s)
                try {
                    $pf = New-Object System.Drawing.SolidBrush($plateFill)
                    try { $g.FillPath($pf, $platePath) } finally { $pf.Dispose() }
                    $pe = New-Object System.Drawing.Pen($plateEdge, [Math]::Max(3 * $s, 1.0))
                    try { $g.DrawPath($pe, $platePath) } finally { $pe.Dispose() }
                } finally { $platePath.Dispose() }
            }

            # Flat "0z0 geometric" battery glyph scaled to fill the canvas (offset 0,0 — the glyph
            # IS the icon here, unlike the wizard banners which place it on a larger composition).
            # The stroke floors are what make the 16 px frame legible: below ~31 px the body
            # outline and the guard line would otherwise render sub-pixel and wash out.
            Draw-BatteryGlyph $g 0 0 $s $plan.Palette -MinBodyPen 1.6 -MinGuardPen 2.0
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
    $treatment = if ($plan.Plated) { "plated (product palette, dark plate)" } else { "transparent" }
    if (-not $plan.Plated -and $HighContrast) { $treatment = "transparent (ink palette)" }
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

# ── Also emit a 256x256 transparent PNG for in-app use ────────────────────────
# The Settings pane-footer <Image> references ms-appx:///Assets/AppIcon.png; the .ico can't be
# bound directly there. Same 256-unit geometry, single frame. Skipped with -HighContrast (that run
# only produces the dense SetupIcon.ico and must not overwrite the app's product-palette PNG).
if (-not $HighContrast) {
    $pngPath = Join-Path (Split-Path $OutPath -Parent) "AppIcon.png"
    [System.IO.File]::WriteAllBytes($pngPath, [byte[]](New-IconFramePng 256))
    $pngBytes = (Get-Item $pngPath).Length
    Write-Host "==> Wrote $pngPath ($pngBytes bytes, 256x256)" -ForegroundColor Green
}
