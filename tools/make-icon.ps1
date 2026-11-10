<#
.SYNOPSIS
  Draws the Ronin icon (gold diamond on charcoal, same design as the tray icon) and writes ronin-host\ronin.ico
  with every size Windows asks for (16-256 px, PNG-compressed entries).
#>
param([string]$Out = (Join-Path (Split-Path $PSScriptRoot -Parent) "ronin-host\ronin.ico"))
Add-Type -AssemblyName System.Drawing
$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$gold = [Drawing.Color]::FromArgb(0xc9, 0xa2, 0x4a)
$glow = [Drawing.Color]::FromArgb(0xf2, 0xc3, 0x5b)
$ink  = [Drawing.Color]::FromArgb(0x16, 0x17, 0x1a)

function Diamond([float]$c, [float]$r) {
    [Drawing.PointF[]]@((New-Object Drawing.PointF $c, ($c - $r)), (New-Object Drawing.PointF ($c + $r), $c),
                        (New-Object Drawing.PointF $c, ($c + $r)), (New-Object Drawing.PointF ($c - $r), $c))
}

$pngs = foreach ($s in $sizes) {
    $bmp = New-Object Drawing.Bitmap $s, $s, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([Drawing.Color]::Transparent)
    $c = $s / 2; $r = $s * 0.46
    $outer = Diamond $c $r
    $bg = New-Object Drawing.SolidBrush $ink; $g.FillPolygon($bg, $outer)
    $pw = [Math]::Max(1.2, $s / 14)
    $pen = New-Object Drawing.Pen $gold, $pw; $pen.LineJoin = 'Miter'; $g.DrawPolygon($pen, (Diamond $c ($r - $pw / 2)))
    if ($s -ge 48) { # a faint inner line on the big sizes
        $thin = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(90, $gold)), ([Math]::Max(1, $s / 64)); $g.DrawPolygon($thin, (Diamond $c ($r * 0.72)))
    }
    $r2 = $r * 0.40
    $grad = New-Object Drawing.Drawing2D.LinearGradientBrush (New-Object Drawing.RectangleF ($c - $r2), ($c - $r2), (2 * $r2), (2 * $r2)), $glow, $gold, 90
    $g.FillPolygon($grad, (Diamond $c $r2))
    $g.Dispose()
    $ms = New-Object IO.MemoryStream
    if ($s -ge 256) { $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png) }
    else {
        # classic DIB entry (readable everywhere): BITMAPINFOHEADER, 32-bit BGRA bottom-up, then an all-zero AND mask
        $bw = New-Object IO.BinaryWriter $ms
        $maskRow = [int]([Math]::Ceiling($s / 32.0) * 4)
        $bw.Write([uint32]40); $bw.Write([int32]$s); $bw.Write([int32]($s * 2)); $bw.Write([uint16]1); $bw.Write([uint16]32)
        $bw.Write([uint32]0); $bw.Write([uint32]($s * $s * 4 + $maskRow * $s)); $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
        for ($y = $s - 1; $y -ge 0; $y--) { for ($x = 0; $x -lt $s; $x++) { $p = $bmp.GetPixel($x, $y); $bw.Write([byte]$p.B); $bw.Write([byte]$p.G); $bw.Write([byte]$p.R); $bw.Write([byte]$p.A) } }
        $bw.Write((New-Object byte[] ($maskRow * $s)))
        $bw.Flush()
    }
    $bmp.Dispose()
    , $ms.ToArray()
}

# ICO container: header, one directory entry per image, then the PNG data
$fs = [IO.File]::Create($Out); $w = New-Object IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $b = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()
Write-Host "wrote $Out"
