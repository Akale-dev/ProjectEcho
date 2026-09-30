[CmdletBinding()]
param()
# Original ProjectEcho artwork. Regenerates PNG-backed ICO sizes without external tools.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\ProjectEcho.App\Assets'
$frames = [Collections.Generic.List[byte[]]]::new()
$sizes = @(16, 24, 32, 48, 64, 128, 256)
foreach ($size in $sizes) {
    $canvas = [Drawing.Bitmap]::new(1024, 1024)
    $graphics = [Drawing.Graphics]::FromImage($canvas)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.ScaleTransform(4, 4)
    $background = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#153d49'))
    $shape = [Drawing.Drawing2D.GraphicsPath]::new()
    $shape.AddArc(8, 8, 112, 112, 180, 90)
    $shape.AddArc(136, 8, 112, 112, 270, 90)
    $shape.AddArc(136, 136, 112, 112, 0, 90)
    $shape.AddArc(8, 136, 112, 112, 90, 90)
    $shape.CloseFigure()
    $graphics.FillPath($background, $shape)
    $wave = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#f6fafb'), 18)
    $wave.StartCap = $wave.EndCap = [Drawing.Drawing2D.LineCap]::Round
    $graphics.DrawLine($wave, 64, 110, 64, 146)
    $graphics.DrawLine($wave, 96, 79, 96, 177)
    $graphics.DrawLine($wave, 128, 99, 128, 157)
    $echo = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#73d9c5'), 13)
    $echo.StartCap = $echo.EndCap = [Drawing.Drawing2D.LineCap]::Round
    $graphics.DrawBezier($echo, 159, 95, 179, 117, 179, 139, 159, 161)
    $graphics.DrawBezier($echo, 183, 73, 216.3333, 109.6667, 216.3333, 146.3333, 183, 183)
    $record = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#f07772'))
    $graphics.FillEllipse($record, 180, 36, 24, 24)
    $bitmap = [Drawing.Bitmap]::new($size, $size)
    $scaled = [Drawing.Graphics]::FromImage($bitmap)
    $scaled.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $scaled.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $scaled.DrawImage($canvas, 0, 0, $size, $size)
    $buffer = [IO.MemoryStream]::new()
    $bitmap.Save($buffer, [Drawing.Imaging.ImageFormat]::Png)
    $frames.Add($buffer.ToArray())
    if ($size -eq 256) { $bitmap.Save((Join-Path $assetDirectory 'ProjectEcho.png'), [Drawing.Imaging.ImageFormat]::Png) }
    $buffer.Dispose(); $scaled.Dispose(); $bitmap.Dispose(); $record.Dispose(); $echo.Dispose(); $wave.Dispose(); $shape.Dispose(); $background.Dispose(); $graphics.Dispose(); $canvas.Dispose()
}
$stream = [IO.File]::Create((Join-Path $assetDirectory 'ProjectEcho.ico'))
$writer = [IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + $sizes.Count * 16
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$index].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$index].Length
    }
    foreach ($frame in $frames) { $writer.Write($frame) }
} finally { $writer.Dispose(); $stream.Dispose() }
Write-Output ('Created original ProjectEcho icon: ' + ($sizes -join ', ') + ' px')
