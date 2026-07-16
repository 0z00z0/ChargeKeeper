<#
.SYNOPSIS
    Generates the ChargeKeeper installer's ZeroZero-studio wizard images
    (WizardImageFile + WizardSmallImageFile) as 24-bit BMPs.

.DESCRIPTION
    Draws the banners natively with System.Drawing (GDI+) — there is no SVG rasteriser on the
    build machine, the same constraint scripts\make-appicon.ps1 and the 0z0-design asset scripts
    work around. The geometry matches installer\wizard\*.svg (the design reference) and reuses the
    [Ø] studio-mark geometry from 0z0-design; the battery glyph itself comes from the shared
    scripts\BatteryGlyph.ps1 (see that file for what must stay in sync with it).

    Output: installer\wizard\wizimg-492x942.bmp   (large side banner, base 164x314 @ 300 %)
            installer\wizard\wizsmall-165x174.bmp (small header,      base 55x58   @ 300 %)
    ONE bitmap each, rendered at the top of the DPI range — see the emit section for why a
    per-DPI variant list is the wrong shape here.

    BMPs are 24-bit (opaque background — no alpha needed) which Inno Setup accepts.

.EXAMPLE
    .\installer\make-wizard-images.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root    = Split-Path $PSScriptRoot -Parent          # repo root

# Battery-glyph geometry + the Product/Dense palettes, shared with scripts\make-appicon.ps1.
# Also supplies New-RoundedRectPath, which the [Ø] studio mark below uses.
. (Join-Path $root "scripts\BatteryGlyph.ps1")
$outDir  = Join-Path $PSScriptRoot "wizard"
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

# ── Brand palette (0z0-design/design-language.md) ─────────────────────────────
function C([int]$r,[int]$g,[int]$b) { [System.Drawing.Color]::FromArgb(255,$r,$g,$b) }
$cBg      = C 0x0a 0x0f 0x17
$cGlow    = C 0x15 0x26 0x3a   # blue-black — the [Ø] mark's OWN plate glow (Draw-Mark); studio-mark rule
$cGlowSteel = C 0x16 0x23 0x2c # steel-tinted — the banner BACKGROUND glow only (flat muted framing)
$cPlate   = C 0x12 0x20 0x2f
$cBorder  = C 0x1a 0x28 0x40
$cText    = C 0xdd 0xe6 0xf4
$cMuted   = C 0x64 0x78 0x8f
$cTeal    = C 0x27 0xe0 0xc8
$cBlue    = C 0x11 0xa9 0xd6
$cPurple  = C 0x7b 0x8c 0xff
$cIndigo  = C 0x3f 0x5b 0xe0
$cAmber   = C 0xd8 0xa6 0x57
# ── ChargeKeeper product palette (== GaugePalette) ────────────────────────────
# Aliases into the shared table, for the flat accent bars this script draws around the glyph —
# the bars are "a quiet echo of the app's GaugePalette", so they must track it, not shadow it.
$cSteel   = $BatteryGlyphPalettes.Product.Body
$cSage    = $BatteryGlyphPalettes.Product.Fill
$cTerra   = $BatteryGlyphPalettes.Product.Guard

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

function Fill-FlatBar($g,[float]$x,[float]$y,[float]$w,[float]$h,$color) {
    $b = New-Object System.Drawing.SolidBrush($color)
    try { $g.FillRectangle($b,$x,$y,$w,$h) } finally { $b.Dispose() }
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
            $pg.CenterColor    = $cGlowSteel
            $pg.SurroundColors = @($cBg)
            $pg.FocusScales    = New-Object System.Drawing.PointF(0.15,0.05)
            $g.FillPath($pg,$bgPath)
        } finally { $pg.Dispose(); $bgPath.Dispose() }

        # Flat muted product framing (no gradients) — the flat-iconography rule (0z0-design/icon-library.md).
        # Top: a single flat SteelBlue bar. Bottom: three flat segments SteelBlue/Sage/Terracotta,
        # a quiet echo of the app's GaugePalette. Both 3 units tall (min 2 px).
        [float]$barH = [Math]::Max(2.0, 3*$k)
        Fill-FlatBar $g 0 0 $w $barH $cSteel
        [float]$seg = $w/3.0
        Fill-FlatBar $g 0                ($h-$barH) $seg          $barH $cSteel
        Fill-FlatBar $g $seg             ($h-$barH) $seg          $barH $cSage
        Fill-FlatBar $g ($seg*2)         ($h-$barH) ($w-$seg*2)   $barH $cTerra

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
                Draw-BatteryGlyph $g (($w-$glyphW)/2) (138*$k) ($glyphW/256.0) $BatteryGlyphPalettes.Product

                # "ChargeKeeper" wordmark BELOW the battery, with a clear gap. No tagline under it.
                $g.DrawString("ChargeKeeper",$wordFont,$tb,(New-Object System.Drawing.RectangleF(0,(236*$k),$w,(28*$k))),$fmtC)
            } finally { $tb.Dispose(); $mb.Dispose() }
        } finally { $studioFont.Dispose(); $tagFont.Dispose(); $wordFont.Dispose(); $fmtC.Dispose() }
    } finally { $g.Dispose() }
    return $bmp
}

# ── Small header image (base 55x58) — product battery glyph on WHITE ──────────
# The inner wizard pages are the light/modern Inno theme, so this clears to WHITE (not the studio
# dark) to blend in rather than float as a dark box. The app's light-steel palette washes out on
# white, so this variant uses the shared Dense palette. Passing it as an argument is why this
# function no longer save/restores $script:cSteel et al around the call.
function Render-Small([int]$w,[int]$h) {
    $bmp = New-Object System.Drawing.Bitmap($w,$h,[System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = New-Graphics $bmp
    try {
        [float]$k = $w / 55.0
        $g.Clear([System.Drawing.Color]::White)
        # battery glyph ~46 units wide, centred.
        $glyphW = 46*$k
        Draw-BatteryGlyph $g (($w-$glyphW)/2) (($h-$glyphW)/2) ($glyphW/256.0) $BatteryGlyphPalettes.Dense
    } finally {
        $g.Dispose()
    }
    return $bmp
}

function Save-Bmp($bmp,$path) { $bmp.Save($path,[System.Drawing.Imaging.ImageFormat]::Bmp); $bmp.Dispose() }

# ── Emit the wizard images ────────────────────────────────────────────────────
# We ship a SINGLE high-resolution "hero" bitmap for each of WizardImageFile and
# WizardSmallImageFile rather than a comma-separated per-DPI variant list. Reason: on a
# mixed-DPI setup (e.g. a 100 % external monitor as primary + a 175 % laptop panel) Inno
# selects/loads the wizard bitmap for the monitor Setup STARTS on, then merely *stretches*
# that loaded bitmap when the window is shown on / dragged to a higher-DPI monitor — an
# UPSCALE, which is exactly what made the banner text look soft. A single bitmap rendered at
# the top of the DPI range (300 %) means Inno can only ever DOWNSCALE it, and downscaling
# stays crisp at every scaling factor from 100 % to 300 %.
#
# This script used to also emit 100/125/150/175/200 % variants of each. Nothing referenced them:
# they were ten tracked BMPs (~1.95 MB) rewritten on every run, justified as "keeping the design
# reference SVGs honest" — which they cannot do, since rendering a bitmap nobody opens or compares
# validates nothing. The base sizes they were derived from survive as the render functions'
# divisors ($w/164.0, $w/55.0), which is where that information actually mattered.
$heroLarge = @(492,942)   # 300 % of the 164x314 base — WizardImageFile
$heroSmall = @(165,174)   # 300 % of the 55x58   base — WizardSmallImageFile

Write-Host "==> Rendering wizard banner (300 % hero)..." -ForegroundColor Cyan
$p = Join-Path $outDir ("wizimg-{0}x{1}.bmp" -f $heroLarge[0],$heroLarge[1])
Save-Bmp (Render-Large $heroLarge[0] $heroLarge[1]) $p
Write-Host ("    {0}" -f $p)

Write-Host "==> Rendering small header image (300 % hero)..." -ForegroundColor Cyan
$p = Join-Path $outDir ("wizsmall-{0}x{1}.bmp" -f $heroSmall[0],$heroSmall[1])
Save-Bmp (Render-Small $heroSmall[0] $heroSmall[1]) $p
Write-Host ("    {0}" -f $p)

Write-Host "==> Done." -ForegroundColor Green
