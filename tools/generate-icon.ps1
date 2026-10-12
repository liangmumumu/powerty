# Generates app.ico for Powerty: flat minimal style (ChatGPT-inspired).
# A single hollow "T": outline stroke + interior filled with dense 45-degree
# left-leaning diagonal hatch lines (thinner than the outline, one shade apart).
# Default: black outline on transparent background (for light backgrounds).
# -Inverted: white outline + light gray hatch (for dark taskbars/backgrounds).
param(
    [switch]$Inverted,
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) "..\src\Powerty.App")
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$outputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$pngDirectory = Join-Path $env:TEMP ("tt-icon-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $pngDirectory | Out-Null

$sizes = 16, 24, 32, 48, 64, 128, 256
if ($Inverted) {
    $borderColor = [System.Drawing.Color]::FromArgb(255, 255, 255, 255)
    $hatchColor = [System.Drawing.Color]::FromArgb(255, 179, 179, 179)
} else {
    $borderColor = [System.Drawing.Color]::FromArgb(255, 0, 0, 0)
    $hatchColor = [System.Drawing.Color]::FromArgb(255, 77, 77, 77)
}

foreach ($size in $sizes) {
    $scale = $size / 256.0
    $bitmap = New-Object System.Drawing.Bitmap($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.Clear([System.Drawing.Color]::Transparent)

    # T silhouette geometry in a 256-space (bar on top, stem centered below).
    # Enlarged: the previous design used left 46 / top 50 / width 164 / height 156;
    # this fills more of the canvas for legibility at small sizes.
    $tLeft = 30.0
    $tTop = 34.0
    $tWidth = 196.0
    $barHeight = 48.0
    $stemWidth = 66.0
    $tBottom = 222.0
    $stemLeft = $tLeft + (($tWidth - $stemWidth) / 2.0)

    [System.Drawing.PointF[]]$outline = @(
        (New-Object System.Drawing.PointF([float]($tLeft * $scale), [float]($tTop * $scale))),
        (New-Object System.Drawing.PointF([float](($tLeft + $tWidth) * $scale), [float]($tTop * $scale))),
        (New-Object System.Drawing.PointF([float](($tLeft + $tWidth) * $scale), [float](($tTop + $barHeight) * $scale))),
        (New-Object System.Drawing.PointF([float](($stemLeft + $stemWidth) * $scale), [float](($tTop + $barHeight) * $scale))),
        (New-Object System.Drawing.PointF([float](($stemLeft + $stemWidth) * $scale), [float]($tBottom * $scale))),
        (New-Object System.Drawing.PointF([float]($stemLeft * $scale), [float]($tBottom * $scale))),
        (New-Object System.Drawing.PointF([float]($stemLeft * $scale), [float](($tTop + $barHeight) * $scale))),
        (New-Object System.Drawing.PointF([float]($tLeft * $scale), [float](($tTop + $barHeight) * $scale)))
    )

    $tPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tPath.AddPolygon($outline)

    # Interior hatch: clip to the T area, then draw dense left-leaning 45-degree lines.
    $regionPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $regionPath.AddRectangle((New-Object System.Drawing.RectangleF(
        [float]($tLeft * $scale), [float]($tTop * $scale),
        [float]($tWidth * $scale), [float]($barHeight * $scale))))
    $regionPath.AddRectangle((New-Object System.Drawing.RectangleF(
        [float]($stemLeft * $scale), [float](($tTop + $barHeight) * $scale),
        [float]($stemWidth * $scale), [float](($tBottom - $tTop - $barHeight) * $scale))))

    $graphics.SetClip($regionPath)
    $hatchWidth = [Math]::Max(0.8, 5.0 * $scale)
    $hatchSpacing = [Math]::Max(1.8, 10.0 * $scale)
    $hatchPen = New-Object System.Drawing.Pen($hatchColor, [float]$hatchWidth)
    $hatchPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Flat
    $hatchPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Flat
    for ($d = -2.0 * $size; $d -le $size; $d += $hatchSpacing) {
        # "\" direction: enters from the top edge, exits at the bottom-right (left-leaning).
        $graphics.DrawLine(
            $hatchPen,
            (New-Object System.Drawing.PointF([float]$d, 0.0)),
            (New-Object System.Drawing.PointF([float]($d + 2.0 * $size), [float](2.0 * $size))))
    }

    $graphics.ResetClip()
    $hatchPen.Dispose()
    $regionPath.Dispose()

    # Black outline on top of the hatch.
    $borderWidth = [Math]::Max(1.0, 18.0 * $scale)
    $borderPen = New-Object System.Drawing.Pen($borderColor, [float]$borderWidth)
    $borderPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Miter
    $graphics.DrawPath($borderPen, $tPath)

    $borderPen.Dispose()
    $tPath.Dispose()
    $graphics.Dispose()
    $bitmap.Save((Join-Path $pngDirectory "icon_$size.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

$entries = foreach ($size in $sizes) {
    [pscustomobject]@{ Size = $size; Bytes = [System.IO.File]::ReadAllBytes((Join-Path $pngDirectory "icon_$size.png")) }
}

$stream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($stream)
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($entry in $entries) {
    $dimension = if ($entry.Size -ge 256) { 0 } else { $entry.Size }
    $writer.Write([byte]$dimension)
    $writer.Write([byte]$dimension)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$entry.Bytes.Length)
    $writer.Write([uint32]$offset)
    $offset += $entry.Bytes.Length
}
foreach ($entry in $entries) {
    $writer.Write($entry.Bytes)
}
$writer.Flush()

$iconPath = Join-Path $outputDirectory "app.ico"
[System.IO.File]::WriteAllBytes($iconPath, $stream.ToArray())
$writer.Dispose()
$stream.Dispose()
Remove-Item -LiteralPath $pngDirectory -Recurse -Force

Write-Host "Icon written: $iconPath ($((Get-Item $iconPath).Length) bytes, $($sizes.Count) sizes)"
