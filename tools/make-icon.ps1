# Generates src\Assets\netledger.ico and src\Assets\logo.png (original artwork).
# Run with Windows PowerShell 5.1:  powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
param([string]$OutDir = (Join-Path $PSScriptRoot '..\src\Assets'))

Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null

function New-RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Get-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    $bg = New-RoundRect 0 0 ($size - 1) ($size - 1) ($size * 0.22)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 22, 33, 44))), $bg)

    $teal  = [System.Drawing.Color]::FromArgb(255, 46, 196, 182)
    $amber = [System.Drawing.Color]::FromArgb(255, 244, 162, 97)
    $pad = $size * 0.2
    $bw  = $size * 0.15
    $gap = ($size - 2 * $pad - 3 * $bw) / 2
    $base = $size * 0.78
    $heights = @(0.30, 0.52, 0.40)
    $colors  = @($teal, $teal, $amber)
    for ($i = 0; $i -lt 3; $i++) {
        $h = $size * $heights[$i]
        $x = $pad + $i * ($bw + $gap)
        $rr = New-RoundRect $x ($base - $h) $bw $h ([Math]::Max(1, $bw * 0.3))
        $g.FillPath((New-Object System.Drawing.SolidBrush $colors[$i]), $rr)
    }
    $penW = [Math]::Max(1, $size * 0.04)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(200, 230, 237, 243)), $penW
    $g.DrawLine($pen, $pad * 0.8, $base + $penW, $size - $pad * 0.8, $base + $penW)
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = @(16, 24, 32, 48, 64, 256)
$images = @()
foreach ($s in $sizes) { $images += ,(Get-IconPng $s) }

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $images[$i].Length
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$len); $w.Write([UInt32]$offset)
    $offset += $len
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'netledger.ico'), $out.ToArray())
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'logo.png'), (Get-IconPng 128))
Write-Output "Wrote icon and logo to $OutDir"
