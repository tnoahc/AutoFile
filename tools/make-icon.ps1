# Regenerates src\AutoFile.App\app.ico: a blue rounded square with "AF", at the sizes Windows asks for.
# Small sizes are stored as 32-bit bitmaps and 256px as PNG, which is what Windows expects in an .ico.
param([string]$Output = (Join-Path $PSScriptRoot "..\src\AutoFile.App\app.ico"))

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = "Stop"

function New-Frame([int]$size) {
    $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = "AntiAlias"
    $g.TextRenderingHint = "AntiAliasGridFit"
    $g.Clear([System.Drawing.Color]::Transparent)

    $inset = [Math]::Max(0.5, $size / 32.0)
    $extent = $size - 2 * $inset
    $radius = $extent * 0.22
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($inset, $inset, 2 * $radius, 2 * $radius, 180, 90)
    $path.AddArc($inset + $extent - 2 * $radius, $inset, 2 * $radius, 2 * $radius, 270, 90)
    $path.AddArc($inset + $extent - 2 * $radius, $inset + $extent - 2 * $radius, 2 * $radius, 2 * $radius, 0, 90)
    $path.AddArc($inset, $inset + $extent - 2 * $radius, 2 * $radius, 2 * $radius, 90, 90)
    $path.CloseFigure()
    $fill = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x1F, 0x6F, 0xB2))
    $g.FillPath($fill, $path)

    $font = New-Object System.Drawing.Font "Segoe UI", ([float]($size * 0.44)), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $format = New-Object System.Drawing.StringFormat
    $format.Alignment = "Center"
    $format.LineAlignment = "Center"
    $rect = New-Object System.Drawing.RectangleF 0, ($size * 0.02), $size, $size
    $g.DrawString("AF", $font, [System.Drawing.Brushes]::White, $rect, $format)
    $g.Dispose()
    return $bitmap
}

function Get-DibBytes($bitmap) {
    $size = $bitmap.Width
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ms
    $maskRow = [int]([Math]::Ceiling($size / 32.0) * 4)
    # BITMAPINFOHEADER; height is doubled to cover the colour and mask planes.
    $w.Write([int]40); $w.Write([int]$size); $w.Write([int]($size * 2))
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]0)
    $w.Write([int]($size * $size * 4 + $maskRow * $size))
    $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
    for ($y = $size - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $size; $x++) {
            $c = $bitmap.GetPixel($x, $y)
            $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A)
        }
    }
    $w.Write((New-Object byte[] ($maskRow * $size)))
    $w.Flush()
    return $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = foreach ($size in $sizes) {
    $bitmap = New-Frame $size
    if ($size -eq 256) {
        $png = New-Object System.IO.MemoryStream
        $bitmap.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
        , $png.ToArray()
    } else {
        , (Get-DibBytes $bitmap)
    }
    $bitmap.Dispose()
}

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dimension); $w.Write([byte]$dimension); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]$images[$i].Length); $w.Write([int]$offset)
    $offset += $images[$i].Length
}
foreach ($image in $images) { $w.Write([byte[]]$image) }
$w.Flush()
[System.IO.File]::WriteAllBytes([System.IO.Path]::GetFullPath($Output), $out.ToArray())
Write-Host "Wrote $Output"
