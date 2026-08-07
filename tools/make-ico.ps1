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

param(
    [Parameter(Mandatory)] [string] $Source,
    [Parameter(Mandatory)] [string] $Destination
)

Add-Type -AssemblyName System.Drawing

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)

$src = [System.Drawing.Bitmap]::new($Source)
$frames = @()

foreach ($size in $sizes) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($src, (New-Object System.Drawing.Rectangle 0, 0, $size, $size))
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
