<#
.SYNOPSIS
    The ChargeKeeper "0z0 steel battery" glyph — the ONE PowerShell rendering of it.
    Dot-source this; it is not runnable on its own.

.DESCRIPTION
    There is no SVG rasteriser on the build machine, so every shipped bitmap is redrawn natively
    with System.Drawing (GDI+) from the geometry that brand\chargekeeper-icon.svg describes. That
    geometry used to be copy-pasted into each generator, which meant moving (say) the guard line
    was four coordinated edits with nothing to catch a miss. It now lives here once, and the two
    generators dot-source it:

        scripts\make-appicon.ps1        -> Assets\AppIcon.ico / .png, Assets\SetupIcon.ico
        installer\make-wizard-images.ps1 -> installer\wizard\wiz*.bmp

    THE FOUR REPRESENTATIONS:
      1. brand\chargekeeper-icon.svg   — the authoritative vector; change it FIRST.
      2. this file                     — both PowerShell generators.
      3. Helpers\IconGenerator.cs      — the runtime tray icon (C#; cannot share this code).
      4. installer\wizard\*.svg        — design references for the wizard banners only.

    Tests\BrandMarkGeometryTests.cs pins 1-3 together: it parses $BatteryGlyphGeometry below and
    the SVG's attributes, and probes IconGenerator's own render. Representation 4 is a design
    reference that nothing generates from, and no test covers it.

    Representation 3 carries a SECOND height set for the live tray icon (TraySlotHeights), taller
    than the brand's so a 16 px square slot is not half empty. That set is the tray's alone — this
    file and the vector state the brand's own proportions, and the same test pins the two apart.

    Callers own their own surface (plates, backgrounds, banners, text). This file owns the glyph
    and the palettes, nothing else.

.NOTES
    Byte-neutrality: the shipped artefacts are tracked in git, so a change here that alters
    rendering shows up as a binary diff. Re-run both generators and check `git status` after
    touching anything below.
#>

Add-Type -AssemblyName System.Drawing

# ── Palettes ──────────────────────────────────────────────────────────────────
# The same three roles, in three densities. Each surface picks a density by CONTRAST, not by
# taste — a brand tint change is one edit here rather than three hand-tuned copies.
#
#   Body  — battery body outline + cap (the structure)
#   Fill  — interior charge fill
#   Guard — the guard line crossing the body
#
# The measured reasoning for each density lives with the surface that needs it: the Ink/Product
# split is argued in make-appicon.ps1's .DESCRIPTION (SetupIcon lands on two opposite chromes);
# the Dense variant is argued in installer\README.md (Inno's light modern inner pages).
$BatteryGlyphPalettes = @{
    # ChargeKeeper's product palette == GaugePalette (SteelBlue / SageGreen / Terracotta), the
    # same values Helpers\IconGenerator.cs reads from GaugePalette. Light tones: they read on the
    # DARK chrome the app lives on (its own #0a0f17 title bar, the taskbar, the wizard banner).
    Product = @{
        Body  = [System.Drawing.Color]::FromArgb(0x7F, 0xA8, 0xB8)
        Fill  = [System.Drawing.Color]::FromArgb(0x7A, 0xB8, 0x8F)
        Guard = [System.Drawing.Color]::FromArgb(0xC9, 0x92, 0x6B)
    }
    # Dense: the product tones deepened enough to hold on WHITE. Used by the installer's
    # wizard-small header, which clears to white to blend into Inno's light modern inner pages.
    Dense = @{
        Body  = [System.Drawing.Color]::FromArgb(0x3F, 0x63, 0x74)
        Fill  = [System.Drawing.Color]::FromArgb(0x4F, 0x8F, 0x67)
        Guard = [System.Drawing.Color]::FromArgb(0xB5, 0x77, 0x45)
    }
    # Ink: denser still. Darker than Dense because a 16 px title-bar icon needs more separation
    # than a 55 px header glyph does. Used only by SetupIcon.ico's 16 px frame.
    Ink = @{
        Body  = [System.Drawing.Color]::FromArgb(0x1C, 0x33, 0x3F)
        Fill  = [System.Drawing.Color]::FromArgb(0x36, 0x6B, 0x4A)
        Guard = [System.Drawing.Color]::FromArgb(0x99, 0x59, 0x2C)
    }
}

# ── Geometry ──────────────────────────────────────────────────────────────────
# brand\chargekeeper-icon.svg on its 256-unit reference canvas, one figure per key. Declared as
# data rather than inlined into the drawing calls because Tests\BrandMarkGeometryTests.cs parses
# this block and compares every value with the SVG's attributes and with Helpers\IconGenerator.cs.
#
# Keep the shape of each line — "Name = <number>", one per line — or the test stops seeing the key
# and fails loudly rather than silently passing.
#
# Body, cap and guard share the centre line y = 128; the fill band is the body's inner rect inset
# by ~14 units on every side. The ink spans y 66..190, i.e. 48 % of the canvas — the mark is
# landscape, and every surface this file feeds gives it room around it. The live tray icon uses a
# taller set of its own (IconGenerator's TraySlotHeights); it does not belong here.
$BatteryGlyphGeometry = @{
    Canvas      = 256.0

    BodyX       = 15.0
    BodyY       = 80.0
    BodyW       = 191.0
    BodyH       = 96.0
    BodyRadius  = 6.0
    BodyPen     = 13.0

    CapX        = 221.0
    CapY        = 106.0
    CapW        = 20.0
    CapH        = 44.0
    CapRadius   = 3.0

    FillX       = 36.0
    FillY       = 101.0
    FillW       = 113.24
    FillH       = 55.0
    FillRadius  = 3.0
    FillAlpha   = 230.0

    GuardX      = 161.16
    GuardTop    = 66.0
    GuardBottom = 190.0
    GuardPen    = 9.0
}

# Rounded rect as a GraphicsPath; radius clamped to half the shorter side so tiny frames can't
# produce arcs larger than the rect itself (GDI+ folds them over on themselves otherwise).
function New-RoundedRectPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $r = [Math]::Min($r, [Math]::Min($w, $h) / 2)
    $d = $r * 2
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x,           $y,           $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y,           $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d,   0, 90)
    $p.AddArc($x,           $y + $h - $d, $d, $d,  90, 90)
    $p.CloseFigure()
    return $p
}

<#
.SYNOPSIS
    Draws the battery glyph on a 256-unit reference sub-canvas at ($ox,$oy), scaled by $s
    (target pixels per reference unit).

.PARAMETER Palette
    One of $BatteryGlyphPalettes (Product / Dense / Ink) — a hashtable with Body/Fill/Guard.

.PARAMETER MinBodyPen
.PARAMETER MinGuardPen
    Stroke-width floors in target pixels. The icon generator passes 1.6 / 2.0 so the outline and
    the guard line survive the 16 px frame; the wizard banners render far above those sizes and
    leave them at 0, i.e. purely proportional. The floors are a clamp, never an override — at any
    size where the proportional width already exceeds the floor they do nothing.
#>
function Draw-BatteryGlyph {
    param(
        $g,
        [float]    $ox,
        [float]    $oy,
        [float]    $s,
        [hashtable]$Palette,
        [double]   $MinBodyPen  = 0.0,
        [double]   $MinGuardPen = 0.0
    )

    # Every figure comes from $BatteryGlyphGeometry above, which is brand\chargekeeper-icon.svg.
    $m = $BatteryGlyphGeometry

    # Battery body outline: flat stroke, round line-join.
    $bodyPath = New-RoundedRectPath ($ox + $m.BodyX * $s) ($oy + $m.BodyY * $s) `
                                    ($m.BodyW * $s) ($m.BodyH * $s) ($m.BodyRadius * $s)
    try {
        $bodyPen = New-Object System.Drawing.Pen($Palette.Body, [Math]::Max($m.BodyPen * $s, $MinBodyPen))
        try {
            $bodyPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
            $g.DrawPath($bodyPen, $bodyPath)
        } finally { $bodyPen.Dispose() }
    } finally { $bodyPath.Dispose() }

    # Battery cap (positive terminal): solid, same tone as the body.
    $capPath = New-RoundedRectPath ($ox + $m.CapX * $s) ($oy + $m.CapY * $s) `
                                   ($m.CapW * $s) ($m.CapH * $s) ($m.CapRadius * $s)
    try {
        $cap = New-Object System.Drawing.SolidBrush($Palette.Body)
        try { $g.FillPath($cap, $capPath) } finally { $cap.Dispose() }
    } finally { $capPath.Dispose() }

    # Interior charge fill: solid at ~90 % opacity (alpha 230).
    $fillPath = New-RoundedRectPath ($ox + $m.FillX * $s) ($oy + $m.FillY * $s) `
                                    ($m.FillW * $s) ($m.FillH * $s) ($m.FillRadius * $s)
    try {
        $fillBrush = New-Object System.Drawing.SolidBrush(
            [System.Drawing.Color]::FromArgb([int]$m.FillAlpha, $Palette.Fill))
        try { $g.FillPath($fillBrush, $fillPath) } finally { $fillBrush.Dispose() }
    } finally { $fillPath.Dispose() }

    # Guard line crossing the body — flat/butt caps (NOT round); round caps would bulge past the
    # line's declared ink extent at the top and bottom, which the vector does not do.
    $limitPen = New-Object System.Drawing.Pen($Palette.Guard, [Math]::Max($m.GuardPen * $s, $MinGuardPen))
    try {
        $limitPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Flat
        $limitPen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Flat
        $g.DrawLine($limitPen, ($ox + $m.GuardX * $s), ($oy + $m.GuardTop    * $s),
                               ($ox + $m.GuardX * $s), ($oy + $m.GuardBottom * $s))
    } finally { $limitPen.Dispose() }
}
