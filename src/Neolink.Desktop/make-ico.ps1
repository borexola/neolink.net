# Builds the desktop app's multi-resolution icon from the web UI's favicon, so the
# shell, the tray and the installer all wear the same logo as the browser tab.
# Run once after the logo changes:  pwsh devtools/make-ico.ps1
# Every entry is a PNG-compressed frame (Vista+ reads those at any size), which
# keeps the file small and the 256px frame crisp.
param(
    [string]$Source = "$PSScriptRoot/../src/Neolink.WebClient/wwwroot/favicon.png",
    [string]$Target = "$PSScriptRoot/../src/Neolink.Desktop/neolink.ico"
)

Add-Type -AssemblyName System.Drawing

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$src = [System.Drawing.Image]::FromFile((Resolve-Path $Source))
$frames = @()

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($src, 0, 0, $s, $s)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $frames += , @{ Size = $s; Bytes = $ms.ToArray() }
    $ms.Dispose()
}
$src.Dispose()

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0)                  # reserved
$w.Write([uint16]1)                  # type: icon
$w.Write([uint16]$frames.Count)

# 6 bytes of header + 16 bytes per directory entry, then the frames back to back.
$offset = 6 + (16 * $frames.Count)
foreach ($f in $frames) {
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }   # 0 means 256 in an ICO
    $w.Write([byte]$dim); $w.Write([byte]$dim)
    $w.Write([byte]0)                # palette entries (0 = truecolour)
    $w.Write([byte]0)                # reserved
    $w.Write([uint16]1)              # colour planes
    $w.Write([uint16]32)             # bits per pixel
    $w.Write([uint32]$f.Bytes.Length)
    $w.Write([uint32]$offset)
    $offset += $f.Bytes.Length
}
foreach ($f in $frames) { $w.Write($f.Bytes) }
$w.Flush()

$dir = Split-Path -Parent $Target
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
[System.IO.File]::WriteAllBytes((Join-Path (Resolve-Path $dir) (Split-Path -Leaf $Target)), $out.ToArray())
$w.Dispose(); $out.Dispose()

Write-Host "wrote $Target ($($frames.Count) frames)"
