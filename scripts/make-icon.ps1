<#
.SYNOPSIS
    Draws the app icon: assets\icon.ico (16-256 px, PNG frames) and assets\logo.png (256 px).

.DESCRIPTION
    A blue cloud with two white sync arrows. Re-run after changing the drawing; the outputs are committed.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\scripts\make-icon.ps1
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$assets = Join-Path $PSScriptRoot '..\assets'
New-Item -ItemType Directory -Force $assets | Out-Null

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 16.0

    # Cloud: three circles and a base (winding fill, so overlaps stay solid), in a top-down blue gradient.
    $dy = 1.6 * $s
    $cloud = New-Object System.Drawing.Drawing2D.GraphicsPath ([System.Drawing.Drawing2D.FillMode]::Winding)
    $cloud.AddEllipse(0.5 * $s, 5.2 * $s + $dy, 6.4 * $s, 6.4 * $s)
    $cloud.AddEllipse(3.7 * $s, 1.4 * $s + $dy, 8.4 * $s, 8.4 * $s)
    $cloud.AddEllipse(8.9 * $s, 4.4 * $s + $dy, 6.6 * $s, 6.6 * $s)
    $cloud.AddRectangle((New-Object System.Drawing.RectangleF (3.7 * $s), (8.0 * $s + $dy), (8.8 * $s), (3.6 * $s)))
    $rect = New-Object System.Drawing.RectangleF 0, (1.0 * $s + $dy), $size, (11 * $s)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(79, 172, 254)), ([System.Drawing.Color]::FromArgb(21, 101, 192)), 90
    $g.FillPath($brush, $cloud)

    # Sync arrows: two arcs of one circle, each with an arrow head.
    if ($size -ge 24) {
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([Math]::Max(1.6, 0.9 * $s))
        $pen.StartCap = 'Round'
        $pen.CustomEndCap = New-Object System.Drawing.Drawing2D.AdjustableArrowCap 2.2, 2.2
        $r = 2.7 * $s
        $cx = 8.1 * $s; $cy = 6.9 * $s + $dy
        $arc = New-Object System.Drawing.RectangleF ($cx - $r), ($cy - $r), (2 * $r), (2 * $r)
        $g.DrawArc($pen, $arc, 200, 130)
        $g.DrawArc($pen, $arc, 20, 130)
        $pen.Dispose()
    }

    $brush.Dispose(); $cloud.Dispose(); $g.Dispose()
    return $bmp
}

function Get-PngBytes($bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$frames = foreach ($size in $sizes) { $b = New-IconBitmap $size; , (Get-PngBytes $b); $b.Dispose() }

# ICO: header, one directory entry per frame, then the PNG frames.
$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ico
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$frames[$i].Length); $w.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($frame in $frames) { $w.Write($frame) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets 'icon.ico'), $ico.ToArray())
[System.IO.File]::WriteAllBytes((Join-Path $assets 'logo.png'), $frames[-1])
Get-ChildItem $assets | Format-Table Name, Length
