# Generates the application icon: a deep blue calendar carrying a red record dot.
#
# The .ico is committed, but it is generated rather than hand-drawn so it can be
# re-rendered and adjusted instead of being an opaque binary nobody can edit.
# Re-run this after changing any of the colours or proportions below.
#
#   .\src\make-icon.ps1
#
# Two variants, and that is the whole point of generating it. At 16 and 20 pixels
# a row of ring marks turns to mush, so below 32px the mark is simplified to the
# body, the header band and the dot. At 32px and up the ring marks are drawn.
# Drawing one picture and scaling it down gives a smudge in the taskbar.
#
# The container is written by hand because System.Drawing's Icon API cannot save
# a multi-image .ico. Sizes up to 48 are written as uncompressed DIBs and the
# larger ones as PNGs: DIB is what every shell surface reads without complaint,
# and PNG is what keeps 256 from being a quarter of a megabyte.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# Named with a suffix because PowerShell variable names are case-insensitive:
# a local $body would silently overwrite a $Body, and did.
$BodyColor = [System.Drawing.Color]::FromArgb(255, 31, 78, 121)     # deep blue
$BandColor = [System.Drawing.Color]::FromArgb(255, 46, 117, 182)    # header band
$MarkColor = [System.Drawing.Color]::White                          # ring marks
$RecordColor = [System.Drawing.Color]::FromArgb(255, 226, 59, 48)   # record dot

$Sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

function New-RoundedPath {
    param([single]$X, [single]$Y, [single]$W, [single]$H, [single]$R)

    $d = [single]($R * 2)
    if ($d -gt $W) { $d = $W }
    if ($d -gt $H) { $d = $H }

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($X, $Y, $d, $d, 180, 90)
    $path.AddArc(($X + $W - $d), $Y, $d, $d, 270, 90)
    $path.AddArc(($X + $W - $d), ($Y + $H - $d), $d, $d, 0, 90)
    $path.AddArc($X, ($Y + $H - $d), $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconBitmap {
    param([int]$Size)

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $m = [single]([Math]::Max(0.5, $Size * 0.045))
    $x = [single]$m
    $y = [single]$m
    $w = [single]($Size - 2 * $m)
    $h = $w
    $r = [single]([Math]::Max(1.0, $w * 0.20))

    $bodyPath = New-RoundedPath -X $x -Y $y -W $w -H $h -R $r
    $brushBody = New-Object System.Drawing.SolidBrush($BodyColor)
    $g.FillPath($brushBody, $bodyPath)

    # The header band is clipped to the body so its top corners stay round.
    $bandH = [single]([Math]::Max(2.0, $h * 0.30))
    $state = $g.Save()
    $g.SetClip($bodyPath)
    $brushBand = New-Object System.Drawing.SolidBrush($BandColor)
    $g.FillRectangle($brushBand, $x, $y, $w, $bandH)
    $g.Restore($state)

    # Ring marks: four when there is room, three when there is less, none below
    # 32px where they would merge into a single grey smear.
    $marks = 0
    if ($Size -ge 64) { $marks = 4 } elseif ($Size -ge 32) { $marks = 3 }

    if ($marks -gt 0) {
        $markSide = [single]([Math]::Max(1.5, $bandH * 0.36))
        $gap = [single](($w - ($marks * $markSide)) / ($marks + 1))
        $markY = [single]($y + ($bandH - $markSide) / 2)
        $brushMark = New-Object System.Drawing.SolidBrush($MarkColor)

        for ($i = 1; $i -le $marks; $i++) {
            $markX = [single]($x + ($gap * $i) + ($markSide * ($i - 1)))
            $markPath = New-RoundedPath -X $markX -Y $markY -W $markSide -H $markSide -R ([single]($markSide / 2))
            $g.FillPath($brushMark, $markPath)
            $markPath.Dispose()
        }

        $brushMark.Dispose()
    }

    # The record dot, centred in the body below the band. It is proportionally
    # larger on the smallest sizes, where a true-to-scale dot disappears.
    $dotR = [single]($w * 0.20)
    if ($Size -lt 32) { $dotR = [single]($w * 0.26) }
    $dotR = [single]([Math]::Max(1.5, $dotR))

    $cx = [single]($x + $w / 2)
    $cy = [single]($y + $bandH + ($h - $bandH) / 2)
    $brushDot = New-Object System.Drawing.SolidBrush($RecordColor)
    $g.FillEllipse($brushDot, ($cx - $dotR), ($cy - $dotR), ($dotR * 2), ($dotR * 2))

    $brushDot.Dispose()
    $brushBand.Dispose()
    $brushBody.Dispose()
    $bodyPath.Dispose()
    $g.Dispose()

    return $bmp
}

function Get-DibBytes {
    param([System.Drawing.Bitmap]$Bitmap)

    $w = $Bitmap.Width
    $h = $Bitmap.Height
    $xorSize = $w * 4 * $h
    $maskStride = [int]([Math]::Floor(($w + 31) / 32) * 4)
    $maskSize = $maskStride * $h
    $buf = New-Object byte[] (40 + $xorSize + $maskSize)

    # BITMAPINFOHEADER. Height is doubled because the DIB carries an XOR image
    # and then a 1bpp AND mask, stacked.
    [Array]::Copy([BitConverter]::GetBytes([int]40), 0, $buf, 0, 4)
    [Array]::Copy([BitConverter]::GetBytes([int]$w), 0, $buf, 4, 4)
    [Array]::Copy([BitConverter]::GetBytes([int]($h * 2)), 0, $buf, 8, 4)
    [Array]::Copy([BitConverter]::GetBytes([int16]1), 0, $buf, 12, 2)
    [Array]::Copy([BitConverter]::GetBytes([int16]32), 0, $buf, 14, 2)

    # 32bpp BGRA, bottom-up.
    $p = 40
    for ($y = $h - 1; $y -ge 0; $y--) {
        for ($px = 0; $px -lt $w; $px++) {
            $c = $Bitmap.GetPixel($px, $y)
            $buf[$p] = $c.B
            $buf[$p + 1] = $c.G
            $buf[$p + 2] = $c.R
            $buf[$p + 3] = $c.A
            $p += 4
        }
    }

    # AND mask, also bottom-up, one bit per pixel, rows padded to four bytes.
    # A set bit means transparent. Modern shells use the alpha channel and
    # ignore this, but leaving it out shows black corners in older surfaces.
    $maskBase = 40 + $xorSize
    for ($y = $h - 1; $y -ge 0; $y--) {
        $rowBase = $maskBase + (($h - 1 - $y) * $maskStride)
        for ($px = 0; $px -lt $w; $px++) {
            if ($Bitmap.GetPixel($px, $y).A -lt 128) {
                $i = $rowBase + [int]([Math]::Floor($px / 8))
                $buf[$i] = $buf[$i] -bor (1 -shl (7 - ($px % 8)))
            }
        }
    }

    return $buf
}

function Get-PngBytes {
    param([System.Drawing.Bitmap]$Bitmap)

    $ms = New-Object System.IO.MemoryStream
    $Bitmap.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    return $bytes
}

$outPath = Join-Path $PSScriptRoot 'PanoptoScheduler.App\Assets\PanoptoScheduler.ico'
$outDir = Split-Path -Parent $outPath
if (-not (Test-Path -LiteralPath $outDir)) {
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
}

$images = @()
foreach ($size in $Sizes) {
    $bmp = New-IconBitmap -Size $size
    # Cast to byte[]: a function returning an array hands its elements to the
    # pipeline one at a time, so the caller would otherwise collect an Object[].
    if ($size -le 48) {
        $data = [byte[]](Get-DibBytes -Bitmap $bmp)
        $kind = 'DIB'
    } else {
        $data = [byte[]](Get-PngBytes -Bitmap $bmp)
        $kind = 'PNG'
    }
    $bmp.Dispose()
    $images += [pscustomobject]@{ Size = $size; Data = $data; Kind = $kind }
}

# ICONDIR, then one ICONDIRENTRY per image, then the image payloads.
$offset = 6 + (16 * $images.Count)
$file = New-Object System.Collections.Generic.List[byte]
$file.AddRange([BitConverter]::GetBytes([int16]0))              # reserved
$file.AddRange([BitConverter]::GetBytes([int16]1))              # type: icon
$file.AddRange([BitConverter]::GetBytes([int16]$images.Count))

foreach ($img in $images) {
    $dim = [byte]0
    if ($img.Size -lt 256) { $dim = [byte]$img.Size }           # 0 means 256
    $file.Add($dim)
    $file.Add($dim)
    $file.Add([byte]0)                                          # palette colours
    $file.Add([byte]0)                                          # reserved
    $file.AddRange([BitConverter]::GetBytes([int16]1))          # planes
    $file.AddRange([BitConverter]::GetBytes([int16]32))         # bits per pixel
    $file.AddRange([BitConverter]::GetBytes([int]$img.Data.Length))
    $file.AddRange([BitConverter]::GetBytes([int]$offset))
    $offset += $img.Data.Length
}

foreach ($img in $images) { $file.AddRange($img.Data) }

[System.IO.File]::WriteAllBytes($outPath, $file.ToArray())

Write-Host ("Wrote {0}" -f $outPath)
Write-Host ("  {0} bytes total" -f $file.Count)
foreach ($img in $images) {
    Write-Host ("  {0,3}x{0,-3} {1}  {2,7} bytes" -f $img.Size, $img.Kind, $img.Data.Length)
}
