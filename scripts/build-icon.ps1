param([string]$OutputDirectory = "")

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root "assets" }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

Add-Type -AssemblyName System.Drawing

function New-DocVistaIconBitmap([int]$Size)
{
    $bitmap = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.Clear([System.Drawing.Color]::Transparent)

    $scale = $Size / 256.0
    $background = New-Object System.Drawing.Drawing2D.GraphicsPath
    $radius = 46 * $scale
    $diameter = $radius * 2
    $background.AddArc(8 * $scale, 8 * $scale, $diameter, $diameter, 180, 90)
    $background.AddArc((248 * $scale) - $diameter, 8 * $scale, $diameter, $diameter, 270, 90)
    $background.AddArc((248 * $scale) - $diameter, (248 * $scale) - $diameter, $diameter, $diameter, 0, 90)
    $background.AddArc(8 * $scale, (248 * $scale) - $diameter, $diameter, $diameter, 90, 90)
    $background.CloseFigure()
    $backgroundBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 41, 55, 66))
    $graphics.FillPath($backgroundBrush, $background)

    $paper = New-Object System.Drawing.Drawing2D.GraphicsPath
    $paper.AddPolygon([System.Drawing.PointF[]]@(
        ([System.Drawing.PointF]::new(65 * $scale, 42 * $scale)),
        ([System.Drawing.PointF]::new(154 * $scale, 42 * $scale)),
        ([System.Drawing.PointF]::new(195 * $scale, 83 * $scale)),
        ([System.Drawing.PointF]::new(195 * $scale, 214 * $scale)),
        ([System.Drawing.PointF]::new(65 * $scale, 214 * $scale))
    ))
    $paperBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 244, 240, 234))
    $graphics.FillPath($paperBrush, $paper)

    $fold = New-Object System.Drawing.Drawing2D.GraphicsPath
    $fold.AddPolygon([System.Drawing.PointF[]]@(
        ([System.Drawing.PointF]::new(154 * $scale, 42 * $scale)),
        ([System.Drawing.PointF]::new(195 * $scale, 83 * $scale)),
        ([System.Drawing.PointF]::new(154 * $scale, 83 * $scale))
    ))
    $foldBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 128, 163, 200))
    $graphics.FillPath($foldBrush, $fold)

    $accentBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 128, 163, 200))
    $mutedBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 94, 90, 81))
    $graphics.FillRectangle($accentBrush, 86 * $scale, 105 * $scale, 88 * $scale, 14 * $scale)
    $graphics.FillRectangle($mutedBrush, 86 * $scale, 137 * $scale, 88 * $scale, 10 * $scale)
    $graphics.FillRectangle($mutedBrush, 86 * $scale, 161 * $scale, 62 * $scale, 10 * $scale)

    $accentBrush.Dispose()
    $mutedBrush.Dispose()
    $foldBrush.Dispose()
    $paperBrush.Dispose()
    $backgroundBrush.Dispose()
    $fold.Dispose()
    $paper.Dispose()
    $background.Dispose()
    $graphics.Dispose()
    return $bitmap
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = foreach ($size in $sizes)
{
    $bitmap = New-DocVistaIconBitmap $size
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    [PSCustomObject]@{ Size = $size; Bytes = $stream.ToArray() }
    $stream.Dispose()
}

$iconPath = Join-Path $OutputDirectory "DocVista.ico"
$file = [System.IO.File]::Create($iconPath)
$writer = New-Object System.IO.BinaryWriter($file)
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$images.Count)
$offset = 6 + (16 * $images.Count)
foreach ($image in $images)
{
    $dimension = if ($image.Size -eq 256) { 0 } else { $image.Size }
    $writer.Write([byte]$dimension)
    $writer.Write([byte]$dimension)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$image.Bytes.Length)
    $writer.Write([uint32]$offset)
    $offset += $image.Bytes.Length
}
foreach ($image in $images) { $writer.Write($image.Bytes) }
$writer.Dispose()
$file.Dispose()

$preview = New-DocVistaIconBitmap 256
$preview.Save((Join-Path $OutputDirectory "DocVista.png"), [System.Drawing.Imaging.ImageFormat]::Png)
$preview.Dispose()

Write-Host "Icon: $iconPath"
