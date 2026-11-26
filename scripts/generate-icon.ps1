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
foreach ($size in @(16, 20, 24, 32, 48, 64, 128, 256)) {
    $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    # Match the two directions of the notification area's bar display.
    $border = [Drawing.Pen]::new([Drawing.Color]::FromArgb(169, 179, 190), 1)
    $receive = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(0, 140, 62))
    $send = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(210, 24, 35))
    $stream = [IO.MemoryStream]::new()
    $frameWriter = [IO.BinaryWriter]::new($stream)
    try {
        $graphics.Clear([Drawing.Color]::White)
        $graphics.DrawRectangle($border, 0, 0, ($size - 1), ($size - 1))
        $laneWidth = [int][Math]::Floor(($size - 3) / 2)
        $receiveHeight = [int][Math]::Floor(($size - 2) * 2 / 3)
        $sendHeight = [int][Math]::Floor(($size - 2) / 2)
        $graphics.FillRectangle($receive, 1, 1, $laneWidth, $receiveHeight)
        $graphics.FillRectangle($send, ($size - 1 - $laneWidth), ($size - 1 - $sendHeight), $laneWidth, $sendHeight)
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
