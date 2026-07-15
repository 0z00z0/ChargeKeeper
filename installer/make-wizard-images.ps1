<#
.SYNOPSIS
    Generates the ChargeKeeper installer's ZeroZero-studio wizard images
    (WizardImageFile + WizardSmallImageFile) as 24-bit BMPs.

.DESCRIPTION
    Draws the banners natively at each Inno DPI size with System.Drawing (GDI+) — there is
    no SVG rasteriser on the build machine, the same constraint scripts\make-appicon.ps1 and
    the 0z0-design asset scripts work around. The geometry matches installer\wizard\*.svg
    (the design reference) and reuses the [Ø] studio-mark geometry from 0z0-design and the
    ChargeKeeper battery-glyph geometry from brand\chargekeeper-icon.svg.

    Output: installer\wizard\wizimg-{WxH}.bmp   (large side banner, base 164x314)
            installer\wizard\wizsmall-{WxH}.bmp  (small header,      base 55x58)
    Each at 100/125/150/175/200 % so Inno can pick the best for the display DPI.

    BMPs are 24-bit (opaque dark background — no alpha needed) which Inno Setup accepts.

.EXAMPLE
    .\installer\make-wizard-images.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root    = Split-Path $PSScriptRoot -Parent          # repo root
$outDir  = Join-Path $PSScriptRoot "wizard"
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

# ── Brand palette (0z0-design/design-language.md) ─────────────────────────────
function C([int]$r,[int]$g,[int]$b) { [System.Drawing.Color]::FromArgb(255,$r,$g,$b) }
$cBg      = C 0x0a 0x0f 0x17
$cGlow    = C 0x15 0x26 0x3a
$cPlate   = C 0x12 0x20 0x2f
$cBorder  = C 0x1a 0x28 0x40
$cText    = C 0xdd 0xe6 0xf4
$cMuted   = C 0x64 0x78 0x8f
$cTeal    = C 0x27 0xe0 0xc8
$cBlue    = C 0x11 0xa9 0xd6
$cPurple  = C 0x7b 0x8c 0xff
$cIndigo  = C 0x3f 0x5b 0xe0
$cAmber   = C 0xd8 0xa6 0x57
# ── ChargeKeeper product palette (== GaugePalette): 0z0-steel battery glyph ────
$cSteel   = C 0x7f 0xa8 0xb8   # SteelBlue  body outline + cap
$cSage    = C 0x7a 0xb8 0x8f   # Sage       interior charge fill
$cTerra   = C 0xc9 0x92 0x6b   # Terracotta guard line

# ── Brand typeface: Cascadia Mono, loaded privately from the sibling design/shared repo ──
$fontPaths = @(
    (Join-Path $root "..\0z0-shared\src\ZeroZero.Brand.WinUI\Assets\Fonts\CascadiaMono.ttf"),
    (Join-Path $root "..\0z0-design\fonts\CascadiaMono.ttf")
)
$pfc = New-Object System.Drawing.Text.PrivateFontCollection
$brandFamily = $null
foreach ($fp in $fontPaths) {
    if (Test-Path $fp) { $pfc.AddFontFile((Resolve-Path $fp).Path); $brandFamily = $pfc.Families[0]; break }
}
if (-not $brandFamily) {
    Write-Warning "CascadiaMono.ttf not found beside the repo; falling back to Consolas."
    $brandFamily = New-Object System.Drawing.FontFamily("Consolas")
}

function New-RoundedRectPath([float]$x,[float]$y,[float]$w,[float]$h,[float]$r) {
    $r = [Math]::Min($r, [Math]::Min($w,$h)/2); $d = $r*2
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x,$y,$d,$d,180,90)
    $p.AddArc($x+$w-$d,$y,$d,$d,270,90)
    $p.AddArc($x+$w-$d,$y+$h-$d,$d,$d,0,90)
    $p.AddArc($x,$y+$h-$d,$d,$d,90,90)
    $p.CloseFigure(); return $p
}

# Draw the [Ø] studio mark on a 256-unit sub-canvas at (ox,oy), scaled by $s (target-px per unit).
function Draw-Mark($g,[float]$ox,[float]$oy,[float]$s) {
    $pt = { param($x,$y) New-Object System.Drawing.PointF(($ox+$x*$s),($oy+$y*$s)) }
    # Background plate (radial glow)
    $plate = New-RoundedRectPath ($ox+8*$s) ($oy+8*$s) (240*$s) (240*$s) (52*$s)
    try {
        $pg = New-Object System.Drawing.Drawing2D.PathGradientBrush($plate)
        try {
            $pg.CenterPoint   = & $pt 128 96
            $pg.CenterColor   = $cGlow
            $pg.SurroundColors = @($cBg)
            $g.FillPath($pg,$plate)
        } finally { $pg.Dispose() }
    } finally { $plate.Dispose() }

    # Brackets (vertical gradients)
    $mkBracket = {
        param($pts,$cTop,$cBot)
        $rect = New-Object System.Drawing.RectangleF(($ox), ($oy+78*$s), (256*$s), (100*$s))
        $lg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            (New-Object System.Drawing.PointF(0,($oy+78*$s))),
            (New-Object System.Drawing.PointF(0,($oy+178*$s))), $cTop, $cBot)
        try {
            $pen = New-Object System.Drawing.Pen($lg, (15*$s))
            try {
                $pen.LineJoin  = [System.Drawing.Drawing2D.LineJoin]::Round
                $pen.StartCap  = [System.Drawing.Drawing2D.LineCap]::Round
                $pen.EndCap    = [System.Drawing.Drawing2D.LineCap]::Round
                $path = New-Object System.Drawing.Drawing2D.GraphicsPath
                try { $path.AddLines([System.Drawing.PointF[]]$pts); $g.DrawPath($pen,$path) }
                finally { $path.Dispose() }
            } finally { $pen.Dispose() }
        } finally { $lg.Dispose() }
    }
    & $mkBracket @((& $pt 104 78),(& $pt 82 78),(& $pt 82 178),(& $pt 104 178)) $cTeal   $cBlue
    & $mkBracket @((& $pt 152 78),(& $pt 174 78),(& $pt 174 178),(& $pt 152 178)) $cPurple $cIndigo

    # Zero ring (amber, stroke only) + slash
    $ring = New-RoundedRectPath ($ox+112*$s) ($oy+88*$s) (32*$s) (80*$s) (12*$s)
    try {
        $ringPen = New-Object System.Drawing.Pen($cAmber, (10*$s))
        try { $g.DrawPath($ringPen,$ring) } finally { $ringPen.Dispose() }
    } finally { $ring.Dispose() }
    $slashPen = New-Object System.Drawing.Pen($cAmber, (6*$s))
    try {
        $slashPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $slashPen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
        $g.DrawLine($slashPen, ($ox+148*$s),($oy+92*$s), ($ox+108*$s),($oy+164*$s))
    } finally { $slashPen.Dispose() }
}

# Draw the ChargeKeeper battery glyph on a 256-unit sub-canvas at (ox,oy), scaled by $s.
# 0z0-steel: flat SteelBlue outline + cap, Sage interior fill, Terracotta guard line —
# geometry matches brand\chargekeeper-icon.svg (no gradients).
function Draw-Battery($g,[float]$ox,[float]$oy,[float]$s) {
    $bodyPath = New-RoundedRectPath ($ox+15*$s) ($oy+80*$s) (191*$s) (96*$s) (6*$s)
    try {
        $pen = New-Object System.Drawing.Pen($cSteel, (13*$s))
        try { $pen.LineJoin=[System.Drawing.Drawing2D.LineJoin]::Round; $g.DrawPath($pen,$bodyPath) }
        finally { $pen.Dispose() }
    } finally { $bodyPath.Dispose() }

    $capPath = New-RoundedRectPath ($ox+221*$s) ($oy+106*$s) (20*$s) (44*$s) (3*$s)
    try { $cap = New-Object System.Drawing.SolidBrush($cSteel); try { $g.FillPath($cap,$capPath) } finally { $cap.Dispose() } }
    finally { $capPath.Dispose() }

    $fillPath = New-RoundedRectPath ($ox+36*$s) ($oy+101*$s) (110*$s) (55*$s) (3*$s)
    try {
        $fb = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(230,$cSage))
        try { $g.FillPath($fb,$fillPath) } finally { $fb.Dispose() }
    } finally { $fillPath.Dispose() }

    $limPen = New-Object System.Drawing.Pen($cTerra, (9*$s))
    try {
        $limPen.StartCap=[System.Drawing.Drawing2D.LineCap]::Flat; $limPen.EndCap=[System.Drawing.Drawing2D.LineCap]::Flat
        $g.DrawLine($limPen, ($ox+161*$s),($oy+66*$s), ($ox+161*$s),($oy+190*$s))
    } finally { $limPen.Dispose() }
}

function Fill-AccentBar($g,[float]$x,[float]$y,[float]$w,[float]$h) {
    # teal->blue | purple->indigo, the two signature gradients side by side.
    $half = $w/2
    $r1 = New-Object System.Drawing.RectangleF($x,$y,$half,$h)
    $r2 = New-Object System.Drawing.RectangleF(($x+$half),$y,$half,$h)
    $b1 = New-Object System.Drawing.Drawing2D.LinearGradientBrush($r1,$cTeal,$cBlue,[System.Drawing.Drawing2D.LinearGradientMode]::Horizontal)
    $b2 = New-Object System.Drawing.Drawing2D.LinearGradientBrush($r2,$cPurple,$cIndigo,[System.Drawing.Drawing2D.LinearGradientMode]::Horizontal)
    try { $g.FillRectangle($b1,$r1); $g.FillRectangle($b2,$r2) } finally { $b1.Dispose(); $b2.Dispose() }
}

function New-Graphics($bmp) {
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    return $g
}

# ── Large side banner (base 164x314) ──────────────────────────────────────────
function Render-Large([int]$w,[int]$h) {
    $bmp = New-Object System.Drawing.Bitmap($w,$h,[System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = New-Graphics $bmp
    try {
        [float]$k = $w / 164.0   # uniform scale (164:314 aspect preserved)
        # Background: radial glow near the top over the base dark.
        $g.Clear($cBg)
        $bgPath = New-Object System.Drawing.Drawing2D.GraphicsPath
        $bgPath.AddRectangle((New-Object System.Drawing.RectangleF(0,0,$w,$h)))
        $pg = New-Object System.Drawing.Drawing2D.PathGradientBrush($bgPath)
        try {
            $pg.CenterPoint    = New-Object System.Drawing.PointF(($w*0.5),($h*0.22))
            $pg.CenterColor    = $cGlow
            $pg.SurroundColors = @($cBg)
            $pg.FocusScales    = New-Object System.Drawing.PointF(0.15,0.05)
            $g.FillPath($pg,$bgPath)
        } finally { $pg.Dispose(); $bgPath.Dispose() }

        Fill-AccentBar $g 0 0            $w (5*$k)
        Fill-AccentBar $g 0 ($h-5*$k)    $w (5*$k)

        # Layout (base 164x314 units, ×$k). Two stacked blocks with a divider between them, spaced so
        # NOTHING overlaps: the ChargeKeeper battery glyph and its wordmark used to collide — they now
        # sit in the lower product block with a clear gap, and the studio tagline moved up under the
        # studio label where it belongs (it's ZeroZero Software's tagline, not the product's).
        #
        #   STUDIO block:  [Ø] mark  →  "ZeroZero Software"  →  "Small tools. Zero bloat."
        #   ── divider ──
        #   PRODUCT block: battery glyph  →  "ChargeKeeper" wordmark

        # [Ø] mark: 52-unit target box, centred near the top.
        $markW = 52*$k
        Draw-Mark $g (($w-$markW)/2) (22*$k) ($markW/256.0)

        $fmtC = New-Object System.Drawing.StringFormat
        $fmtC.Alignment     = [System.Drawing.StringAlignment]::Center
        $fmtC.LineAlignment = [System.Drawing.StringAlignment]::Center

        $studioFont = New-Object System.Drawing.Font($brandFamily,(11*$k),[System.Drawing.FontStyle]::Regular,[System.Drawing.GraphicsUnit]::Pixel)
        $tagFont    = New-Object System.Drawing.Font($brandFamily,(9*$k), [System.Drawing.FontStyle]::Regular,[System.Drawing.GraphicsUnit]::Pixel)
        $wordFont   = New-Object System.Drawing.Font($brandFamily,(16*$k),[System.Drawing.FontStyle]::Bold,   [System.Drawing.GraphicsUnit]::Pixel)
        try {
            $tb = New-Object System.Drawing.SolidBrush($cText)
            $mb = New-Object System.Drawing.SolidBrush($cMuted)
            try {
                # Studio label + studio tagline (top block, under the [Ø] mark). Rect centres:
                # ~y86 and ~y103 — clear of the mark above (brackets end ~y58) and the divider below.
                $g.DrawString("ZeroZero Software",$studioFont,$tb,(New-Object System.Drawing.RectangleF(0,(78*$k),$w,(16*$k))),$fmtC)
                $g.DrawString("Small tools. Zero bloat.",$tagFont,$mb,(New-Object System.Drawing.RectangleF(0,(96*$k),$w,(14*$k))),$fmtC)

                # Divider between the studio and product blocks.
                $divPen = New-Object System.Drawing.Pen($cBorder,(1*$k))
                try { $g.DrawLine($divPen,(40*$k),(120*$k),($w-40*$k),(120*$k)) } finally { $divPen.Dispose() }

                # Battery glyph: 110-unit box, placed low. Its drawn extent is ~y166..y222; the
                # wordmark below starts ~y242, so glyph and wordmark never overlap.
                $glyphW = 110*$k
                Draw-Battery $g (($w-$glyphW)/2) (138*$k) ($glyphW/256.0)

                # "ChargeKeeper" wordmark BELOW the battery, with a clear gap. No tagline under it.
                $g.DrawString("ChargeKeeper",$wordFont,$tb,(New-Object System.Drawing.RectangleF(0,(236*$k),$w,(28*$k))),$fmtC)
            } finally { $tb.Dispose(); $mb.Dispose() }
        } finally { $studioFont.Dispose(); $tagFont.Dispose(); $wordFont.Dispose(); $fmtC.Dispose() }
    } finally { $g.Dispose() }
    return $bmp
}

# ── Small header image (base 55x58) — product battery glyph on WHITE ──────────
# The inner wizard pages are the light/modern Inno theme, so this clears to WHITE (not the studio
# dark) to blend in rather than float as a dark box. The steel/sage/terracotta glyph reads fine on
# white.
function Render-Small([int]$w,[int]$h) {
    $bmp = New-Object System.Drawing.Bitmap($w,$h,[System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = New-Graphics $bmp
    try {
        [float]$k = $w / 55.0
        $g.Clear([System.Drawing.Color]::White)
        # battery glyph ~46 units wide, centred.
        $glyphW = 46*$k
        Draw-Battery $g (($w-$glyphW)/2) ((($h-$glyphW*(256.0/256.0))/2)) ($glyphW/256.0)
    } finally { $g.Dispose() }
    return $bmp
}

function Save-Bmp($bmp,$path) { $bmp.Save($path,[System.Drawing.Imaging.ImageFormat]::Bmp); $bmp.Dispose() }

# ── Emit all DPI variants ─────────────────────────────────────────────────────
$largeSizes = @(@(164,314),@(205,392),@(246,471),@(287,549),@(328,628))   # 100/125/150/175/200 %
$smallSizes = @(@(55,58),@(69,73),@(83,87),@(96,102),@(110,116))

Write-Host "==> Rendering large wizard banner variants..." -ForegroundColor Cyan
foreach ($sz in $largeSizes) {
    $p = Join-Path $outDir ("wizimg-{0}x{1}.bmp" -f $sz[0],$sz[1])
    Save-Bmp (Render-Large $sz[0] $sz[1]) $p
    Write-Host ("    {0}" -f $p)
}
Write-Host "==> Rendering small header image variants..." -ForegroundColor Cyan
foreach ($sz in $smallSizes) {
    $p = Join-Path $outDir ("wizsmall-{0}x{1}.bmp" -f $sz[0],$sz[1])
    Save-Bmp (Render-Small $sz[0] $sz[1]) $p
    Write-Host ("    {0}" -f $p)
}
Write-Host "==> Done." -ForegroundColor Green
