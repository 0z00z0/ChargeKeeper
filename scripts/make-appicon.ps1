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

    The geometry matches brand\chargekeeper-icon.svg (the authoritative vector),
    expressed on a 256-unit reference canvas and scaled per frame. No background plate —
    fully transparent, battery glyph scaled to fill the canvas. Flat "0z0 geometric" style:
    a squared SteelBlue body+cap, a Sage-green interior fill, and a Terracotta guard line
    (ChargeKeeper's own GaugePalette colours — no gradients). Stroke widths are clamped so
    the battery outline and the guard line stay legible at 16 px.

    After writing, the ICO is round-tripped through System.Drawing.Icon at several
    sizes as a sanity check that the file parses.

.EXAMPLE
    .\scripts\make-appicon.ps1                    # writes Assets\AppIcon.ico + Assets\AppIcon.png
    .\scripts\make-appicon.ps1 -OutPath my.ico    # writes elsewhere
#>
[CmdletBinding()]
param(
    [string] $OutPath = ""   # default: <repo>\Assets\AppIcon.ico
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent
if (-not $OutPath) { $OutPath = Join-Path $root "Assets\AppIcon.ico" }

$sizes = 256, 128, 64, 48, 32, 16

# ── Palette (matches brand\chargekeeper-icon.svg / Helpers\IconGenerator.cs) ──
# ChargeKeeper's own muted product palette (== GaugePalette SteelBlue / SageGreen / Terracotta).
$steelBlue  = [System.Drawing.Color]::FromArgb(0x7F, 0xA8, 0xB8)   # body outline + cap
$sageGreen  = [System.Drawing.Color]::FromArgb(0x7A, 0xB8, 0x8F)   # interior fill
$terracotta = [System.Drawing.Color]::FromArgb(0xC9, 0x92, 0x6B)  # guard line

# Rounded rect as a GraphicsPath; radius clamped to half the shorter side so tiny
# frames can't produce arcs larger than the rect itself.
function New-RoundedRectPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $r = [Math]::Min($r, [Math]::Min($w, $h) / 2)
    $d = $r * 2
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x,          $y,          $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y,          $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d,   0, 90)
    $p.AddArc($x,          $y + $h - $d, $d, $d,  90, 90)
    $p.CloseFigure()
    return $p
}

# Renders one frame and returns it as a PNG byte array.
function New-IconFramePng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        try {
            $g.SmoothingMode   = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $g.Clear([System.Drawing.Color]::Transparent)

            [float]$s = $size / 256.0

            # No background plate — fully transparent (canvas already cleared above). Flat
            # "0z0 geometric" battery glyph scaled to fill the canvas.

            # Battery body outline: flat SteelBlue stroke (clamped ≥1.6 px), round line-join.
            $bodyPath = New-RoundedRectPath (15 * $s) (80 * $s) (191 * $s) (96 * $s) (6 * $s)
            try {
                $bodyPen = New-Object System.Drawing.Pen($steelBlue, [Math]::Max(13 * $s, 1.6))
                try {
                    $bodyPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
                    $g.DrawPath($bodyPen, $bodyPath)
                } finally { $bodyPen.Dispose() }
            } finally { $bodyPath.Dispose() }

            # Battery cap (positive terminal): solid SteelBlue.
            $capPath = New-RoundedRectPath (221 * $s) (106 * $s) (20 * $s) (44 * $s) (3 * $s)
            try {
                $cap = New-Object System.Drawing.SolidBrush($steelBlue)
                try   { $g.FillPath($cap, $capPath) }
                finally { $cap.Dispose() }
            } finally { $capPath.Dispose() }

            # Interior charge fill: solid Sage green at ~90 % opacity (alpha ≈ 230).
            $fillPath = New-RoundedRectPath (36 * $s) (101 * $s) (110 * $s) (55 * $s) (3 * $s)
            try {
                $fillBrush = New-Object System.Drawing.SolidBrush(
                    [System.Drawing.Color]::FromArgb(230, $sageGreen))
                try   { $g.FillPath($fillBrush, $fillPath) }
                finally { $fillBrush.Dispose() }
            } finally { $fillPath.Dispose() }

            # Terracotta guard line crossing the body — flat/butt caps (NOT round).
            # Clamped to ≥2 px so it survives the 16 px frame.
            $limitPen = New-Object System.Drawing.Pen($terracotta, [Math]::Max(9 * $s, 2.0))
            try {
                $limitPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Flat
                $limitPen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Flat
                $g.DrawLine($limitPen, (161 * $s), (66 * $s), (161 * $s), (190 * $s))
            } finally { $limitPen.Dispose() }
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
foreach ($size in $sizes) { $frames.Add([byte[]](New-IconFramePng $size)) }

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
# bound directly there. Same 256-unit geometry, single frame.
$pngPath = Join-Path (Split-Path $OutPath -Parent) "AppIcon.png"
[System.IO.File]::WriteAllBytes($pngPath, [byte[]](New-IconFramePng 256))
$pngBytes = (Get-Item $pngPath).Length
Write-Host "==> Wrote $pngPath ($pngBytes bytes, 256x256)" -ForegroundColor Green
