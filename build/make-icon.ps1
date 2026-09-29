# DSH-App icon generator  (ASCII only -- see AGENTS.md rule 1)
#
#   powershell -ExecutionPolicy Bypass -File build\make-icon.ps1
#
# Writes ui\dsh.ico when it is missing: a rounded badge in the product accent
# colour (#4d6bfe, the same blue ui\app.html uses) with a white "D".
#
# Layout: the small sizes are classic 32-bit DIB entries and 256x256 is a PNG
# entry. That mix is what the shell, the taskbar and System.Drawing.Icon all
# understand without special cases.
#
# An icon that is already there is never overwritten, so real artwork can simply
# be dropped in at ui\dsh.ico. build.ps1 treats a failure here as "build without
# an icon", never as fatal.

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$out  = Join-Path $root 'ui\dsh.ico'

# An icon that is already there is normally left alone, so dropping in real artwork is
# enough to replace the generated one. DSH_ICON_REGEN=1 forces a regeneration while
# iterating on the design.
if ((Test-Path $out) -and -not $env:DSH_ICON_REGEN) {
    Write-Host "icon already present, left as it is: $out"
    exit 0
}

Add-Type -AssemblyName System.Drawing

# The badge is a vertical gradient rather than a flat fill: it reads as noticeably more
# finished above 32px while leaving the silhouette -- the only thing that survives at 16px --
# exactly the same. Light end is the interface accent (#4d6bfe from ui\app.html); dark end is
# the same hue taken down, so the icon still matches the program's own UI.
$brand     = [System.Drawing.Color]::FromArgb(255, 58, 58, 68)
$brandDeep = [System.Drawing.Color]::FromArgb(255, 22, 22, 26)
$dibSize = @(16, 20, 24, 32, 48)
$pngSize = 256

function New-Badge([int]$size) {
    $bmp    = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g      = [System.Drawing.Graphics]::FromImage($bmp)
    $path   = New-Object System.Drawing.Drawing2D.GraphicsPath
    $brush  = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
                  (New-Object System.Drawing.PointF(0, 0)),
                  (New-Object System.Drawing.PointF(0, [float]$size)),
                  $brand, $brandDeep)
    $family = New-Object System.Drawing.FontFamily('Segoe UI')
    $font   = New-Object System.Drawing.Font($family, [float]($size * 0.62), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $fmt    = New-Object System.Drawing.StringFormat
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.Clear([System.Drawing.Color]::Transparent)

        $w = [float]$size
        $d = [float]($size * 0.48)
        $path.AddArc(0, 0, $d, $d, 180, 90)
        $path.AddArc(($w - $d), 0, $d, $d, 270, 90)
        $path.AddArc(($w - $d), ($w - $d), $d, $d, 0, 90)
        $path.AddArc(0, ($w - $d), $d, $d, 90, 90)
        $path.CloseFigure()
        $g.FillPath($brush, $path)

        # A hairline rim just inside the badge edge. On a dark taskbar or title bar a black
        # tile otherwise has no boundary at all -- it merges into the background and only the
        # letter remains visible. The rim keeps the silhouette readable on any background
        # without touching the letter, so it costs nothing at 16x16.
        #
        # Drawn inset by half its width so the stroke sits inside the canvas: stroking the
        # path itself would place half the line outside and clip it away at the edges.
        $rimW = [float]($size * 0.012)
        $rimInset = $rimW / 2
        $rimPath = New-Object System.Drawing.Drawing2D.GraphicsPath
        $rimPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(58, 255, 255, 255), $rimW)
        try {
            $rw = [float]$size
            $rd = [float]($size * 0.48) - $rimInset
            $rimPath.AddArc($rimInset, $rimInset, $rd, $rd, 180, 90)
            $rimPath.AddArc(($rw - $rd - $rimInset), $rimInset, $rd, $rd, 270, 90)
            $rimPath.AddArc(($rw - $rd - $rimInset), ($rw - $rd - $rimInset), $rd, $rd, 0, 90)
            $rimPath.AddArc($rimInset, ($rw - $rd - $rimInset), $rd, $rd, 90, 90)
            $rimPath.CloseFigure()
            $g.DrawPath($rimPen, $rimPath)
        } finally { $rimPen.Dispose(); $rimPath.Dispose() }

        # Design notes, kept because the reasoning is not obvious from the code:
        #
        #  * The mark is the letter D -- the product's initial. Earlier redesigns replaced it
        #    with an abstract power/start glyph and with two strokes meeting at a point. The
        #    second of those rendered as a lambda, a shape several companies use as a logo, so
        #    it was rejected on sight; the power glyph was clear but generic and lost the
        #    identity. The letter won: it is a single bold shape that survives 16x16, which is
        #    the entry the taskbar and title bar actually use.
        #  * Near-black gradient rather than a flat fill, because pure black reads as flat and
        #    dirty at large sizes while a gradient still has depth. The bottom is not #000:
        #    an earlier all-black version merged into a dark taskbar and only the letter
        #    remained visible.
        #  * That is what the hairline rim below is for -- it restores the silhouette against
        #    dark backgrounds without touching the letter, so it costs nothing at 16x16.
        #  * Highlight and shadow alphas scale with size (they fade out below 48px) so the
        #    smallest entries stay crisp instead of turning milky.        # Inner highlight: a soft white wash along the top edge, clipped to the badge. This is
        # what gives the tile the sense of being lit from above. The alpha scales with size so
        # that a 16px entry does not turn milky from a highlight it cannot afford.
        $hlAlpha = [int](56 * [Math]::Min(1.0, $size / 48.0))
        if ($hlAlpha -gt 0) {
            $saved = $g.Clip
            $g.SetClip($path)
            $hl = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
                      (New-Object System.Drawing.PointF(0, 0)),
                      (New-Object System.Drawing.PointF(0, [float]($size * 0.55))),
                      ([System.Drawing.Color]::FromArgb($hlAlpha, 255, 255, 255)),
                      ([System.Drawing.Color]::FromArgb(12, 255, 255, 255)))
            try { $g.FillRectangle($hl, 0, 0, $size, [float]($size * 0.55)) }
            finally { $hl.Dispose(); $g.Clip = $saved }
        }

        # The letter. Raised a hair so it is optically centred (a cap-height glyph in a square
        # box always looks low), and given a soft shadow so it lifts off the badge. Shadow
        # alpha scales with size too -- at 16px it would only muddy the strokes.
        $fmt.Alignment     = [System.Drawing.StringAlignment]::Center
        $fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
        $dy  = [float]($size * 0.014)
        $lay = [int](82 * [Math]::Min(1.0, $size / 48.0))
        if ($lay -gt 0) {
            $shadow = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb($lay, 0, 0, 48))
            try {
                $sb = New-Object System.Drawing.RectangleF(0, [float]($dy + $size * 0.013), $w, $w)
                $g.DrawString('D', $font, $shadow, $sb, $fmt)
            } finally { $shadow.Dispose() }
        }
        $box = New-Object System.Drawing.RectangleF(0, -$dy, $w, $w)
        $g.DrawString('D', $font, [System.Drawing.Brushes]::White, $box, $fmt)
    } finally {
        $g.Dispose()
        $path.Dispose()
        $brush.Dispose()
        $font.Dispose()
        $family.Dispose()
        $fmt.Dispose()
    }
    return $bmp
}

$entries = New-Object System.Collections.ArrayList

foreach ($size in $dibSize) {
    $bmp  = New-Badge $size
    $rect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
    $locked = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $stride = [Math]::Abs($locked.Stride)
        $pixels = New-Object byte[] ($stride * $size)
        [System.Runtime.InteropServices.Marshal]::Copy($locked.Scan0, $pixels, 0, $pixels.Length)
    } finally {
        $bmp.UnlockBits($locked)
        $bmp.Dispose()
    }

    $andStride = [int]([Math]::Ceiling($size / 32.0) * 4)
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    try {
        $bw.Write([UInt32]40)
        $bw.Write([Int32]$size)
        $bw.Write([Int32]($size * 2))
        $bw.Write([UInt16]1)
        $bw.Write([UInt16]32)
        $bw.Write([UInt32]0)
        $bw.Write([UInt32]($size * $size * 4))
        $bw.Write([Int32]0)
        $bw.Write([Int32]0)
        $bw.Write([UInt32]0)
        $bw.Write([UInt32]0)
        for ($y = $size - 1; $y -ge 0; $y--) {
            $bw.Write($pixels, $y * $stride, $size * 4)
        }
        $bw.Write((New-Object byte[] ($andStride * $size)))
        $bw.Flush()
        [void]$entries.Add([pscustomobject]@{ Size = $size; Data = $ms.ToArray() })
    } finally {
        $bw.Dispose()
        $ms.Dispose()
    }
    Write-Host ("  + {0}x{0} DIB" -f $size)
}

$big = New-Badge $pngSize
$png = New-Object System.IO.MemoryStream
try {
    $big.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
} finally {
    $big.Dispose()
}
[void]$entries.Add([pscustomobject]@{ Size = $pngSize; Data = $png.ToArray() })
$png.Dispose()
Write-Host ("  + {0}x{0} PNG" -f $pngSize)

$fs = [System.IO.File]::Create($out)
$fw = New-Object System.IO.BinaryWriter($fs)
try {
    $fw.Write([UInt16]0)
    $fw.Write([UInt16]1)
    $fw.Write([UInt16]$entries.Count)
    $offset = 6 + 16 * $entries.Count
    foreach ($e in $entries) {
        $dim = 0
        if ($e.Size -lt 256) { $dim = $e.Size }
        $fw.Write([Byte]$dim)
        $fw.Write([Byte]$dim)
        $fw.Write([Byte]0)
        $fw.Write([Byte]0)
        $fw.Write([UInt16]1)
        $fw.Write([UInt16]32)
        $fw.Write([UInt32]$e.Data.Length)
        $fw.Write([UInt32]$offset)
        $offset += $e.Data.Length
    }
    foreach ($e in $entries) { $fw.Write($e.Data) }
    $fw.Flush()
} finally {
    $fw.Dispose()
    $fs.Dispose()
}

$check = New-Object System.Drawing.Icon($out)
$side  = $check.Size.Width
$check.Dispose()
Write-Host ("icon written: {0} ({1:N0} bytes, {2} entries, picks {3}x{3})" -f $out, (Get-Item $out).Length, $entries.Count, $side)
