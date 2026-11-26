#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$env:PYTHONUTF8 = '1'
$env:PYTHONIOENCODING = 'utf-8'
$env:CI = 'true'
Add-Type -AssemblyName System.Drawing

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$assetDirectory = Join-Path $root 'assets'
$null = New-Item -ItemType Directory -Path $assetDirectory -Force
$frames = [Collections.Generic.List[object]]::new()
foreach ($size in @(16, 24, 32, 48, 64, 128, 256)) {
    $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    # Match TrafficIcons: opaque white, wide arrows, and fixed colors without glow.
    $border = [Drawing.Pen]::new([Drawing.Color]::FromArgb(150, 150, 150), 1)
    $receive = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(0, 140, 62))
    $send = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(210, 24, 35))
    $receivePath = [Drawing.Drawing2D.GraphicsPath]::new()
    $sendPath = [Drawing.Drawing2D.GraphicsPath]::new()
    $stream = [IO.MemoryStream]::new()
    $frameWriter = [IO.BinaryWriter]::new($stream)
    try {
        $graphics.Clear([Drawing.Color]::White)
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::None
        $graphics.ScaleTransform($size / 32.0, $size / 32.0)
        $graphics.DrawRectangle($border, [single]0.5, [single]0.5, [single]31, [single]31)
        $receivePath.AddPolygon([Drawing.PointF[]]@(
            [Drawing.PointF]::new(4, 4), [Drawing.PointF]::new(12, 4),
            [Drawing.PointF]::new(12, 18), [Drawing.PointF]::new(14, 18),
            [Drawing.PointF]::new(8, 28), [Drawing.PointF]::new(2, 18), [Drawing.PointF]::new(4, 18)))
        $sendPath.AddPolygon([Drawing.PointF[]]@(
            [Drawing.PointF]::new(20, 28), [Drawing.PointF]::new(28, 28),
            [Drawing.PointF]::new(28, 14), [Drawing.PointF]::new(30, 14),
            [Drawing.PointF]::new(24, 4), [Drawing.PointF]::new(18, 14), [Drawing.PointF]::new(20, 14)))
        $graphics.FillPath($receive, $receivePath)
        $graphics.FillPath($send, $sendPath)
        $graphics.Flush()
        $locked = $bitmap.LockBits([Drawing.Rectangle]::new(0, 0, $size, $size), [Drawing.Imaging.ImageLockMode]::ReadOnly, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            if ($locked.Stride -ne $size * 4) { throw 'Unexpected bitmap stride.' }
            $pixels = [byte[]]::new($size * $size * 4)
            [Runtime.InteropServices.Marshal]::Copy($locked.Scan0, $pixels, 0, $pixels.Length)
        } finally { $bitmap.UnlockBits($locked) }
        # Write a true-color DIB frame directly: Icon.Save quantizes HICONs to 16 colors.
        $frameWriter.Write([uint32]40)
        $frameWriter.Write([int32]$size); $frameWriter.Write([int32]($size * 2))
        $frameWriter.Write([uint16]1); $frameWriter.Write([uint16]32)
        1..6 | ForEach-Object { $frameWriter.Write([uint32]0) }
        for ($row = $size - 1; $row -ge 0; $row--) { $frameWriter.Write($pixels, $row * $size * 4, $size * 4) }
        $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
        for ($row = $size - 1; $row -ge 0; $row--) {
            $mask = [byte[]]::new($maskStride)
            for ($column = 0; $column -lt $size; $column++) {
                if ($pixels[($row * $size + $column) * 4 + 3] -eq 0) {
                    $index = [int][Math]::Floor($column / 8.0)
                    $mask[$index] = $mask[$index] -bor (0x80 -shr ($column % 8))
                }
            }
            $frameWriter.Write($mask)
        }
        $frames.Add(@{ Size = $size; Image = $stream.ToArray() })
    } finally {
        $frameWriter.Dispose(); $stream.Dispose()
        $sendPath.Dispose(); $receivePath.Dispose()
        $send.Dispose(); $receive.Dispose()
        $border.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
    }
}

$output = Join-Path $assetDirectory 'SaruMon.ico'
$writer = [IO.BinaryWriter]::new([IO.File]::Create($output))
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Image.Length)
        $writer.Write([uint32]$offset)
        $offset += $frame.Image.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Image) }
} finally { $writer.Dispose() }
Write-Output "Generated: $output"
