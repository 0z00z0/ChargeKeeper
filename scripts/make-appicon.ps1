<#
.SYNOPSIS
    Generates Assets\AppIcon.ico — the ChargeKeeper "Guarded Battery" app icon.

.DESCRIPTION
    Draws the icon natively at each size (256/128/64/48/32/16) with System.Drawing —
    no downscaling, so small frames stay crisp — and assembles a PNG-in-ICO file
    (6-byte header + 16-byte directory entries + PNG frames; a width/height byte of 0
    means 256). PNG-compressed frames are supported by Windows Vista and later.

    The geometry matches brand\chargekeeper-icon.svg (the authoritative vector),
    expressed on a 256-unit reference canvas and scaled per frame. No background plate —
    fully transparent, battery glyph scaled to fill the canvas. Stroke widths are
    clamped so the battery outline and the amber charge-limit line stay legible at 16 px.

    After writing, the ICO is round-tripped through System.Drawing.Icon at several
    sizes as a sanity check that the file parses.

.EXAMPLE
    .\scripts\make-appicon.ps1                    # writes Assets\AppIcon.ico
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
$bodyLight  = [System.Drawing.Color]::FromArgb(0x7B, 0x8C, 0xFF)   # purple (gradient start)
$bodyDark   = [System.Drawing.Color]::FromArgb(0x3F, 0x5B, 0xE0)   # indigo (gradient end)
$limitAmber = [System.Drawing.Color]::FromArgb(0xD8, 0xA6, 0x57)   # charge-limit line

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

            # No background plate — fully transparent (canvas already cleared above). Battery
            # glyph geometry scaled ~1.25x vs. the original inset-in-a-card design so it fills
            # the canvas instead of floating in a smaller background square.

            # Battery body outline: purple→indigo gradient stroke (clamped ≥1.6 px).
            $bodyRect = New-Object System.Drawing.RectangleF((13 * $s), (78 * $s), (195 * $s), (100 * $s))
            $bodyPath = New-RoundedRectPath (13 * $s) (78 * $s) (195 * $s) (100 * $s) (23 * $s)
            try {
                $bodyBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
                    $bodyRect, $bodyLight, $bodyDark,
                    [System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal)
                try {
                    $bodyPen = New-Object System.Drawing.Pen($bodyBrush, [Math]::Max(15 * $s, 1.6))
                    try {
                        $bodyPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
                        $g.DrawPath($bodyPen, $bodyPath)
                    } finally { $bodyPen.Dispose() }
                } finally { $bodyBrush.Dispose() }
            } finally { $bodyPath.Dispose() }

            # Battery cap (positive terminal): solid indigo.
            $capPath = New-RoundedRectPath (221 * $s) (103 * $s) (23 * $s) (50 * $s) (9 * $s)
            try {
                $cap = New-Object System.Drawing.SolidBrush($bodyDark)
                try   { $g.FillPath($cap, $capPath) }
                finally { $cap.Dispose() }
            } finally { $capPath.Dispose() }

            # Interior charge fill: same gradient at ~85 % opacity, to ~80 % of the body.
            $fillRect = New-Object System.Drawing.RectangleF((36 * $s), (101 * $s), (110 * $s), (55 * $s))
            $fillPath = New-RoundedRectPath (36 * $s) (101 * $s) (110 * $s) (55 * $s) (11 * $s)
            try {
                $fillBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
                    $fillRect,
                    [System.Drawing.Color]::FromArgb(217, $bodyLight),
                    [System.Drawing.Color]::FromArgb(217, $bodyDark),
                    [System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal)
                try   { $g.FillPath($fillBrush, $fillPath) }
                finally { $fillBrush.Dispose() }
            } finally { $fillPath.Dispose() }

            # Amber charge-limit line at the 80 % mark, overshooting the body top/bottom.
            # Clamped to ≥2 px so it survives the 16 px frame.
            $limitPen = New-Object System.Drawing.Pen($limitAmber, [Math]::Max(9 * $s, 2.0))
            try {
                $limitPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
                $limitPen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
                $g.DrawLine($limitPen, (161 * $s), (63 * $s), (161 * $s), (193 * $s))
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
