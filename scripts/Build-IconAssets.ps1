param([string]$SourcePath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$projectRoot = Split-Path -Parent $PSScriptRoot
$assetsPath = Join-Path $projectRoot 'Assets'
if (-not $SourcePath) { $SourcePath = Join-Path $assetsPath 'Branding\Amoxtli-master.png' }
$sourceImage = [System.Drawing.Bitmap]::new($SourcePath)

# Mechanical size/format conversion only: preserve the generated artwork and alpha.
function Get-IconPng([int]$Width, [int]$Height, [double]$Fill = 1.0) {
    $bitmap = [System.Drawing.Bitmap]::new($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $memory = [System.IO.MemoryStream]::new()
    $attributes = [System.Drawing.Imaging.ImageAttributes]::new()
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $attributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
        $ratio = [Math]::Min($Width / $sourceImage.Width, $Height / $sourceImage.Height) * $Fill
        $drawWidth = [int][Math]::Round($sourceImage.Width * $ratio)
        $drawHeight = [int][Math]::Round($sourceImage.Height * $ratio)
        $rectangle = [System.Drawing.Rectangle]::new([int](($Width - $drawWidth) / 2), [int](($Height - $drawHeight) / 2), $drawWidth, $drawHeight)
        $graphics.DrawImage($sourceImage, $rectangle, 0, 0, $sourceImage.Width, $sourceImage.Height, [System.Drawing.GraphicsUnit]::Pixel, $attributes)
        $bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
        return ,$memory.ToArray()
    }
    finally { $attributes.Dispose(); $graphics.Dispose(); $bitmap.Dispose(); $memory.Dispose() }
}

function Save-IconPng([string]$Name, [int]$Width, [int]$Height, [double]$Fill = 1.0) {
    [System.IO.File]::WriteAllBytes((Join-Path $assetsPath $Name), (Get-IconPng $Width $Height $Fill))
}

try {
    $scales = @(100, 125, 150, 200, 400)
    foreach ($scale in $scales) {
        $factor = $scale / 100.0
        Save-IconPng "Amoxtli.Square44x44Logo.scale-$scale.png" ([int][Math]::Round(44 * $factor)) ([int][Math]::Round(44 * $factor)) 0.9
        Save-IconPng "Amoxtli.Square150x150Logo.scale-$scale.png" ([int][Math]::Round(150 * $factor)) ([int][Math]::Round(150 * $factor)) 0.8
        Save-IconPng "Amoxtli.Wide310x150Logo.scale-$scale.png" ([int][Math]::Round(310 * $factor)) ([int][Math]::Round(150 * $factor)) 0.8
        Save-IconPng "Amoxtli.SplashScreen.scale-$scale.png" ([int][Math]::Round(620 * $factor)) ([int][Math]::Round(300 * $factor)) 0.7
        Save-IconPng "Amoxtli.StoreLogo.scale-$scale.png" ([int][Math]::Ceiling(50 * $factor)) ([int][Math]::Ceiling(50 * $factor)) 0.9
    }
    foreach ($size in @(16, 20, 24, 32, 40, 48, 64, 128, 256)) {
        Save-IconPng "Amoxtli.Square44x44Logo.targetsize-$size.png" $size $size
        Save-IconPng "Amoxtli.Square44x44Logo.targetsize-${size}_altform-unplated.png" $size $size
        Save-IconPng "Amoxtli.Square44x44Logo.targetsize-${size}_altform-lightunplated.png" $size $size
    }

    # ICO directory with lossless, alpha-preserving PNG payloads for Windows 10/11.
    $iconSizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
    $entries = @($iconSizes | ForEach-Object { ,(Get-IconPng $_ $_) })
    $file = [System.IO.File]::Create((Join-Path $assetsPath 'Amoxtli.ico'))
    $writer = [System.IO.BinaryWriter]::new($file)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$entries.Count)
        $offset = 6 + 16 * $entries.Count
        for ($index = 0; $index -lt $entries.Count; $index++) {
            $sizeByte = if ($iconSizes[$index] -eq 256) { 0 } else { $iconSizes[$index] }
            $writer.Write([byte]$sizeByte); $writer.Write([byte]$sizeByte)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$entries[$index].Length); $writer.Write([uint32]$offset)
            $offset += $entries[$index].Length
        }
        foreach ($entry in $entries) { $writer.Write([byte[]]$entry) }
    }
    finally { $writer.Dispose(); $file.Dispose() }
}
finally { $sourceImage.Dispose() }
Write-Output 'Icono ICO y 52 recursos PNG generados en Assets.'
