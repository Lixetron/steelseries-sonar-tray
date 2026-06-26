Add-Type -AssemblyName System.Drawing

function New-RoundedRectPath {
    param(
        [System.Drawing.RectangleF]$Rect,
        [float]$Radius
    )

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $maxRadius = [Math]::Min($Rect.Width, $Rect.Height) / 2.0
    $radius = [Math]::Min($Radius, $maxRadius)

    if ($radius -le 0.5) {
        $path.AddRectangle($Rect)
        return $path
    }

    $diameter = $radius * 2.0
    $right = $Rect.X + $Rect.Width
    $bottom = $Rect.Y + $Rect.Height

    $path.AddArc($Rect.X, $Rect.Y, $diameter, $diameter, 180, 90)
    $path.AddArc($right - $diameter, $Rect.Y, $diameter, $diameter, 270, 90)
    $path.AddArc($right - $diameter, $bottom - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($Rect.X, $bottom - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-RoundedRectPathFromRectangle {
    param(
        [System.Drawing.Rectangle]$Rect,
        [float]$Radius
    )

    $rectF = New-Object System.Drawing.RectangleF ([float]$Rect.X), ([float]$Rect.Y), ([float]$Rect.Width), ([float]$Rect.Height)
    return New-RoundedRectPath -Rect $rectF -Radius $Radius
}

function Fill-VerticalPill {
    param(
        [System.Drawing.Graphics]$Graphics,
        [System.Drawing.Brush]$Brush,
        [float]$XCenter,
        [float]$YTop,
        [float]$YBottom,
        [float]$Width
    )

    $height = $YBottom - $YTop
    if ($height -le 0.5) {
        return
    }

    $half = $Width / 2.0
    $left = $XCenter - $half
    $radius = [Math]::Min($half, $height / 2.0)
    $rect = New-Object System.Drawing.Rectangle (
        [int][Math]::Floor($left),
        [int][Math]::Floor($YTop),
        [int][Math]::Ceiling($Width),
        [int][Math]::Ceiling($height))
    $path = New-RoundedRectPathFromRectangle -Rect $rect -Radius $radius
    $Graphics.FillPath($Brush, $path)
    $path.Dispose()
}

function Draw-MixerBars {
    param(
        [System.Drawing.Graphics]$Graphics,
        [System.Drawing.Color]$Color,
        [System.Drawing.Rectangle]$Bounds,
        [double]$BarWidthRatio = 0.18,
        [double]$GapRatio = 0.10,
        [switch]$TightCapInset,
        [switch]$SnapToPixels,
        [string]$PixelOffsetMode = "HighQuality"
    )

    $areaWidth = $Bounds.Width
    $barWidth = [Math]::Max(2.0, $areaWidth * $BarWidthRatio)
    $minGap = if ($SnapToPixels) { 1.0 } else { 2.0 }
    $gap = [Math]::Max($minGap, $areaWidth * $GapRatio)
    $capInset = [Math]::Ceiling($barWidth / 2.0)
    if (-not $TightCapInset) {
        $capInset += 1
    }

    $topY = [float]($Bounds.Top + $capInset)
    $baseY = [float]($Bounds.Bottom - $capInset)
    $maxBarHeight = $baseY - $topY
    $heights = @(0.62, 1.0, 0.48)

    $totalWidth = (3 * $barWidth) + (2 * $gap)
    $startX = $Bounds.Left + (($areaWidth - $totalWidth) / 2.0)

    if ($SnapToPixels) {
        $barWidth = [Math]::Round($barWidth)
        $gap = [Math]::Max($minGap, [Math]::Round($gap))
        $totalWidth = (3 * $barWidth) + (2 * $gap)
        $startX = [Math]::Round($Bounds.Left + (($areaWidth - $totalWidth) / 2.0))
    }

    $Graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $Graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $Graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::$PixelOffsetMode

    $brush = New-Object System.Drawing.SolidBrush $Color
    for ($i = 0; $i -lt 3; $i++) {
        $height = [Math]::Max(1.0, $maxBarHeight * $heights[$i])
        $x = $startX + ($i * ($barWidth + $gap)) + ($barWidth / 2.0)
        if ($SnapToPixels) {
            $x = [Math]::Round($x)
        }

        $yTop = $baseY - $height
        Fill-VerticalPill -Graphics $Graphics -Brush $brush -XCenter $x -YTop $yTop -YBottom $baseY -Width $barWidth
    }

    $brush.Dispose()
}

function Get-AppIconLayout {
    param([int]$Size)

    if ($Size -le 20) {
        return @{
            Margin = 0
            CanvasPad = 1
            TileInset = 0.0
            RadiusRatio = 0.22
            BarWidthRatio = 0.15
            GapRatio = 0.08
            BarInset = 1
            TightCapInset = $true
            SnapToPixels = $true
            PixelOffsetMode = "Half"
        }
    }

    if ($Size -le 32) {
        return @{
            Margin = 0
            CanvasPad = 1
            TileInset = 0.0
            RadiusRatio = 0.22
            BarWidthRatio = 0.16
            GapRatio = 0.09
            BarInset = 2
            TightCapInset = $true
            SnapToPixels = $true
            PixelOffsetMode = "Half"
        }
    }

    if ($Size -le 48) {
        return @{
            Margin = 1
            CanvasPad = 0
            TileInset = 0.0
            RadiusRatio = 0.20
            BarWidthRatio = 0.17
            GapRatio = 0.09
            BarInset = 2
            TightCapInset = $true
            SnapToPixels = $true
            PixelOffsetMode = "HighQuality"
        }
    }

    return @{
        Margin = [int]($Size * 0.035)
        CanvasPad = 0
        TileInset = 0.5
        RadiusRatio = 0.19
        BarWidthRatio = 0.18
        GapRatio = 0.10
        BarInset = 0
        TightCapInset = $false
        SnapToPixels = $false
        PixelOffsetMode = "HighQuality"
    }
}

function Get-BarBounds {
    param(
        [System.Drawing.Rectangle]$TileRect,
        [int]$Inset
    )

    if ($Inset -le 0) {
        return $TileRect
    }

    $left = $TileRect.Left + $Inset
    $top = $TileRect.Top + $Inset
    $width = $TileRect.Width - (2 * $Inset)
    $height = $TileRect.Height - (2 * $Inset)
    return New-Object System.Drawing.Rectangle $left, $top, $width, $height
}

function New-MixerBitmap {
    param(
        [int]$Size,
        [bool]$WithTile,
        [System.Drawing.Color]$BarColor
    )

    $bitmap = New-Object System.Drawing.Bitmap $Size, $Size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.Clear([System.Drawing.Color]::Transparent)

    if ($WithTile) {
        $layout = Get-AppIconLayout -Size $Size
        $margin = [Math]::Max($layout.Margin, $layout.CanvasPad)
        $tileSize = $Size - (2 * $margin)
        $tileRect = New-Object System.Drawing.Rectangle $margin, $margin, $tileSize, $tileSize
        $tileInset = [float]$layout.TileInset
        $tileLeft = [float]$margin + $tileInset
        $tileTop = [float]$margin + $tileInset
        $tileWidth = [float]$tileSize - (2.0 * $tileInset)
        $tileHeight = [float]$tileSize - (2.0 * $tileInset)
        $tileRectF = New-Object System.Drawing.RectangleF $tileLeft, $tileTop, $tileWidth, $tileHeight
        $radius = [Math]::Min($tileRectF.Width, $tileRectF.Height) * $layout.RadiusRatio

        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::$($layout.PixelOffsetMode)

        $tileBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 43, 43, 43))
        $tilePath = New-RoundedRectPath -Rect $tileRectF -Radius $radius
        $graphics.FillPath($tileBrush, $tilePath)
        $tileBrush.Dispose()
        $tilePath.Dispose()

        $barBounds = Get-BarBounds -TileRect $tileRect -Inset $layout.BarInset
        Draw-MixerBars -Graphics $graphics -Color $BarColor -Bounds $barBounds `
            -BarWidthRatio $layout.BarWidthRatio `
            -GapRatio $layout.GapRatio `
            -PixelOffsetMode $layout.PixelOffsetMode `
            -TightCapInset:$layout.TightCapInset `
            -SnapToPixels:$layout.SnapToPixels
    }
    else {
        $bounds = New-Object System.Drawing.Rectangle 0, 0, $Size, $Size
        Draw-MixerBars -Graphics $graphics -Color $BarColor -Bounds $bounds
    }

    $graphics.Dispose()
    return $bitmap
}

function Save-PngIco {
    param(
        [scriptblock]$RenderBitmap,
        [string]$PngPath,
        [string]$IcoPath,
        [int[]]$IcoSizes = @(16, 20, 24, 32, 48, 256)
    )

    $preview = & $RenderBitmap 256
    $preview.Save($PngPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $preview.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter $ms

    $writer.Write([int16]0)
    $writer.Write([int16]1)
    $writer.Write([int16]$IcoSizes.Length)

    $imageDataOffset = 6 + (16 * $IcoSizes.Length)
    $pngDatas = New-Object System.Collections.Generic.List[byte[]]

    foreach ($size in $IcoSizes) {
        $bitmap = & $RenderBitmap $size
        $pngMs = New-Object System.IO.MemoryStream
        $bitmap.Save($pngMs, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngDatas.Add($pngMs.ToArray())
        $pngMs.Dispose()
        $bitmap.Dispose()
    }

    foreach ($size in $IcoSizes) {
        $width = if ($size -eq 256) { 0 } else { $size }
        $height = if ($size -eq 256) { 0 } else { $size }
        $data = $pngDatas[$IcoSizes.IndexOf($size)]
        $writer.Write([byte]$width)
        $writer.Write([byte]$height)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([int16]1)
        $writer.Write([int16]32)
        $writer.Write([int32]$data.Length)
        $writer.Write([int32]$imageDataOffset)
        $imageDataOffset += $data.Length
    }

    foreach ($data in $pngDatas) {
        $writer.Write($data)
    }

    [System.IO.File]::WriteAllBytes($IcoPath, $ms.ToArray())
    $writer.Dispose()
    $ms.Dispose()
}

$assetsDir = Join-Path $PSScriptRoot "..\steelseries-sonar-tray\Assets"
$accent = [System.Drawing.Color]::FromArgb(255, 96, 205, 255)
$white = [System.Drawing.Color]::FromArgb(255, 255, 255, 255)
$dark = [System.Drawing.Color]::FromArgb(255, 26, 26, 26)

$trayVariants = @(
    @{ Name = "tray-accent"; Color = $accent },
    @{ Name = "tray-white"; Color = $white },
    @{ Name = "tray-dark"; Color = $dark }
)

foreach ($variant in $trayVariants) {
    $color = $variant.Color
    $renderer = {
        param([int]$Size)
        New-MixerBitmap -Size $Size -WithTile $false -BarColor $color
    }.GetNewClosure()

    Save-PngIco -RenderBitmap $renderer `
        -PngPath (Join-Path $assetsDir ($variant.Name + ".png")) `
        -IcoPath (Join-Path $assetsDir ($variant.Name + ".ico"))
}

$appRenderer = {
    param([int]$Size)
    New-MixerBitmap -Size $Size -WithTile $true -BarColor $accent
}.GetNewClosure()

Save-PngIco -RenderBitmap $appRenderer `
    -PngPath (Join-Path $assetsDir "app-icon.png") `
    -IcoPath (Join-Path $assetsDir "app.ico")

Write-Host "Generated app.ico and tray-accent/white/dark icons in $assetsDir"
