# Builds a multi-resolution .ico from a square PNG.
#
# Windows picks a different size per context — 16px in the title bar, 32 in the
# taskbar, 48 in Explorer, 256 in large-icon views. Shipping one size makes the
# others a scaled-down blur, so every size is rendered separately from the 1024px
# original at high quality.
#
# Each frame is stored as a PNG inside the .ico, which the format has allowed
# since Vista and which keeps the alpha channel intact. A BMP frame would need a
# separate AND mask and would lose the soft glow.
#
# The source is cropped to its content first. Artwork exported at 1024px tends to
# carry a wide transparent margin — this one used half its width on it — and an
# icon that keeps that margin renders visibly smaller than every neighbour in the
# taskbar, because Windows fits the whole canvas into the slot and most of the
# canvas is nothing. Cropping and re-padding to a chosen fraction puts the drawing
# at the size of the icons beside it.

param(
    [Parameter(Mandatory)] [string] $Source,
    [Parameter(Mandatory)] [string] $Destination,

    # Fraction of the frame the artwork should span. Windows' own icons sit at
    # roughly 90% for a squarish glyph; leaving a little room stops the shape
    # touching the edge, which reads as clipped.
    [double] $Fill = 0.92,

    # Alpha at or above which a pixel counts as content. Above the glow, below
    # the shape.
    [int] $AlphaThreshold = 64
)

Add-Type -AssemblyName System.Drawing

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)

$original = [System.Drawing.Bitmap]::new($Source)

# --- Find the tight bounds of the artwork -----------------------------------

$minX = $original.Width; $minY = $original.Height; $maxX = -1; $maxY = -1

$bits = $original.LockBits(
    (New-Object System.Drawing.Rectangle 0, 0, $original.Width, $original.Height),
    [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
    [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

$stride = $bits.Stride
$buffer = New-Object byte[] ($stride * $original.Height)
[System.Runtime.InteropServices.Marshal]::Copy($bits.Scan0, $buffer, 0, $buffer.Length)
$original.UnlockBits($bits)

for ($y = 0; $y -lt $original.Height; $y++) {
    $row = $y * $stride
    for ($x = 0; $x -lt $original.Width; $x++) {
        # BGRA little-endian: alpha is the fourth byte.
        if ($buffer[$row + ($x * 4) + 3] -ge $AlphaThreshold) {
            if ($x -lt $minX) { $minX = $x }
            if ($x -gt $maxX) { $maxX = $x }
            if ($y -lt $minY) { $minY = $y }
            if ($y -gt $maxY) { $maxY = $y }
        }
    }
}

if ($maxX -lt 0) {
    throw "No pixel in '$Source' reaches alpha $AlphaThreshold — nothing to crop to."
}

$contentWidth = $maxX - $minX + 1
$contentHeight = $maxY - $minY + 1

# Square it off around the centre of the content, so the aspect ratio survives
# and the drawing is not stretched into a square frame.
$side = [Math]::Max($contentWidth, $contentHeight)
$centreX = $minX + ($contentWidth / 2.0)
$centreY = $minY + ($contentHeight / 2.0)

$crop = New-Object System.Drawing.RectangleF (
    [float]($centreX - $side / 2.0), [float]($centreY - $side / 2.0), [float]$side, [float]$side)

"content {0}x{1} at ({2},{3}); cropping to {4}x{4} square, drawn at {5:P0} of each frame" -f
    $contentWidth, $contentHeight, $minX, $minY, [int]$side, $Fill

$src = $original
$frames = @()

foreach ($size in $sizes) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # Draw the cropped square into a centred box covering $Fill of the frame, so
    # the artwork lands at a consistent size whatever margin the source carried.
    $drawn = [Math]::Max(1, [int][Math]::Round($size * $Fill))
    $inset = ($size - $drawn) / 2.0

    $g.DrawImage(
        $src,
        (New-Object System.Drawing.RectangleF ([float]$inset), ([float]$inset), ([float]$drawn), ([float]$drawn)),
        $crop,
        [System.Drawing.GraphicsUnit]::Pixel)

    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += , @{ Size = $size; Bytes = $ms.ToArray() }
    $ms.Dispose()
    $bmp.Dispose()
}

$src.Dispose()

# ICONDIR: reserved(2) type(2) count(2), then one 16-byte ICONDIRENTRY each.
$out = [System.IO.File]::Create($Destination)
$w = New-Object System.IO.BinaryWriter($out)

$w.Write([uint16]0)
$w.Write([uint16]1)
$w.Write([uint16]$frames.Count)

$offset = 6 + (16 * $frames.Count)

foreach ($f in $frames) {
    # 256 is written as 0 — the field is one byte.
    $w.Write([byte]($(if ($f.Size -ge 256) { 0 } else { $f.Size })))
    $w.Write([byte]($(if ($f.Size -ge 256) { 0 } else { $f.Size })))
    $w.Write([byte]0)      # palette count: 0 for true colour
    $w.Write([byte]0)      # reserved
    $w.Write([uint16]1)    # colour planes
    $w.Write([uint16]32)   # bits per pixel
    $w.Write([uint32]$f.Bytes.Length)
    $w.Write([uint32]$offset)
    $offset += $f.Bytes.Length
}

foreach ($f in $frames) {
    $w.Write($f.Bytes)
}

$w.Flush()
$w.Dispose()
$out.Dispose()

"wrote {0} ({1:N0} bytes, {2} frames: {3})" -f $Destination,
    (Get-Item $Destination).Length, $frames.Count, ($sizes -join ', ')
