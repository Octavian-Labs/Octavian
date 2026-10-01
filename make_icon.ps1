# Генератор иконки OctavianRSM: обсидиан + золото, ромб «◆» как в шапке окна.
# Пишет Assets\app.ico (мульти-размер) и Assets\icon.png (256).
Add-Type -AssemblyName System.Drawing

$out = "D:\Empire\Octavian\Tools\OctavianRSM\Assets"
New-Item -ItemType Directory -Path $out -Force | Out-Null

function New-IconBitmap([int]$size) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [double]$size / 256.0
    # Скруглённый квадрат — обсидиан
    $rr = 36.0 * $s
    $rx = 8.0 * $s; $ry = 8.0 * $s; $rw = 240.0 * $s; $rh = 240.0 * $s
    $rect = [System.Drawing.RectangleF]::new($rx, $ry, $rw, $rh)
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = $rr * 2
    $path.AddArc($rx, $ry, $d, $d, 180, 90)
    $path.AddArc($rx + $rw - $d, $ry, $d, $d, 270, 90)
    $path.AddArc($rx + $rw - $d, $ry + $rh - $d, $d, $d, 0, 90)
    $path.AddArc($rx, $ry + $rh - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $bg = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        $rect,
        [System.Drawing.Color]::FromArgb(18, 22, 30),
        [System.Drawing.Color]::FromArgb(9, 11, 15), 90)
    $g.FillPath($bg, $path)
    $pen = [System.Drawing.Pen]::new(
        [System.Drawing.Color]::FromArgb(58, 66, 82), [float]([Math]::Max(1.5, 3 * $s)))
    $g.DrawPath($pen, $path)

    # Ромб: грани золота разного тона для объёма
    $cx = 128.0 * $s; $cy = 128.0 * $s
    $hw = 74.0 * $s; $hh = 88.0 * $s
    $top    = [System.Drawing.PointF]::new($cx, $cy - $hh)
    $right  = [System.Drawing.PointF]::new($cx + $hw, $cy)
    $bottom = [System.Drawing.PointF]::new($cx, $cy + $hh)
    $left   = [System.Drawing.PointF]::new($cx - $hw, $cy)
    $center = [System.Drawing.PointF]::new($cx, $cy)

    $goldTop  = [System.Drawing.Color]::FromArgb(240, 200, 94)
    $goldMid  = [System.Drawing.Color]::FromArgb(217, 169, 63)
    $goldSoft = [System.Drawing.Color]::FromArgb(233, 190, 84)
    $goldDark = [System.Drawing.Color]::FromArgb(150, 110, 35)
    $goldDeep = [System.Drawing.Color]::FromArgb(120, 88, 28)

    $g.FillPolygon([System.Drawing.SolidBrush]::new($goldTop),  [System.Drawing.PointF[]]@($top, $right, $center))
    $g.FillPolygon([System.Drawing.SolidBrush]::new($goldSoft), [System.Drawing.PointF[]]@($top, $left, $center))
    $g.FillPolygon([System.Drawing.SolidBrush]::new($goldDark), [System.Drawing.PointF[]]@($center, $left, $bottom))
    $g.FillPolygon([System.Drawing.SolidBrush]::new($goldDeep), [System.Drawing.PointF[]]@($center, $right, $bottom))
    $pen2 = [System.Drawing.Pen]::new(
        [System.Drawing.Color]::FromArgb(60, 44, 14), [float]([Math]::Max(1, 2 * $s)))
    $g.DrawPolygon($pen2, [System.Drawing.PointF[]]@($top, $right, $bottom, $left))

    $g.Dispose()
    return , $bmp
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @()
foreach ($sz in $sizes) {
    $b = New-IconBitmap $sz
    $ms = [System.IO.MemoryStream]::new()
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , $ms.ToArray()
    if ($sz -eq 256) { $b.Save("$out\icon.png") }
    $b.Dispose(); $ms.Dispose()
}

# ICO: заголовок + каталог + PNG-блоки (PNG в ICO поддержан с Vista)
$ico = [System.IO.MemoryStream]::new()
$w = [System.IO.BinaryWriter]::new($ico)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]
    $byte = [byte]($(if ($sz -ge 256) { 0 } else { $sz }))
    $w.Write($byte)                      # width
    $w.Write($byte)                      # height
    $w.Write([byte]0)                    # colors
    $w.Write([byte]0)                    # reserved
    $w.Write([uint16]1)                  # planes
    $w.Write([uint16]32)                 # bpp
    $w.Write([uint32]$pngs[$i].Length)
    $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Flush()
[System.IO.File]::WriteAllBytes("$out\app.ico", $ico.ToArray())
$w.Dispose(); $ico.Dispose()

"OK: $out\app.ico + icon.png"
