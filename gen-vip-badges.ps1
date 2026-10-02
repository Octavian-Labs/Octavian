# gen-vip-badges.ps1 — генерация бейджей VIP-уровней для serveremoji.
# Формы: star / diamond / circle / hexagon; цвет = цвет уровня OctVIP.
# Использование: powershell -File gen-vip-badges.ps1 [-OutDir <путь>]
param(
    [string]$OutDir = 'D:\Octavian Rust\Octavian_Test_Server\423\server\server\octavian\serveremoji'
)
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

# цвета уровней из OctVIP
$levels = [ordered]@{
    base      = '#8A8F98'
    bronze    = '#CD7F32'
    silver    = '#C0C0C0'
    gold      = '#FFD700'
    brilliant = '#4FC3F7'
    moderator = '#59C073'
    admin     = '#E06666'
}
$shapes = @('star','diamond','circle','hex')

function New-Badge([string]$shape, [string]$hex, [string]$file) {
    $size = 96
    $bmp  = New-Object System.Drawing.Bitmap($size, $size)
    $g    = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $color  = [System.Drawing.ColorTranslator]::FromHtml($hex)
    $brush  = New-Object System.Drawing.SolidBrush($color)
    $darker = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, [int]($color.R*0.55), [int]($color.G*0.55), [int]($color.B*0.55)))
    $pen    = New-Object System.Drawing.Pen($darker, 5)
    $cx = 48; $cy = 48; $r = 40

    switch ($shape) {
        'circle' {
            $g.FillEllipse($brush, 8, 8, 80, 80)
            $g.DrawEllipse($pen, 10, 10, 76, 76)
        }
        'diamond' {
            $pts = [System.Drawing.Point[]]@(
                [System.Drawing.Point]::new($cx, 6),
                [System.Drawing.Point]::new(($cx + 40), $cy),
                [System.Drawing.Point]::new($cx, 90),
                [System.Drawing.Point]::new(($cx - 40), $cy)
            )
            $g.FillPolygon($brush, $pts)
            $g.DrawPolygon($pen, $pts)
        }
        'hex' {
            $pts = [System.Drawing.Point[]]@(
                0..5 | ForEach-Object {
                    $a = [Math]::PI/3*$_ - [Math]::PI/6
                    [System.Drawing.Point]::new([int]($cx + $r*[Math]::Cos($a)), [int]($cy + $r*[Math]::Sin($a)))
                }
            )
            $g.FillPolygon($brush, $pts)
            $g.DrawPolygon($pen, $pts)
        }
        default { # star — пятиконечная
            $pts = [System.Drawing.Point[]]@(
                0..9 | ForEach-Object {
                    $rr = if ($_ % 2 -eq 0) { $r } else { [int]($r*0.45) }
                    $a = -[Math]::PI/2 + [Math]::PI*$_/5
                    [System.Drawing.Point]::new([int]($cx + $rr*[Math]::Cos($a)), [int]($cy + $rr*[Math]::Sin($a)))
                }
            )
            $g.FillPolygon($brush, $pts)
            $g.DrawPolygon($pen, $pts)
        }
    }
    $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}

$made = 0
foreach ($lvl in $levels.GetEnumerator()) {
    for ($i = 0; $i -lt $shapes.Count; $i++) {
        $file = Join-Path $OutDir ("vip_{0}_{1}.png" -f $lvl.Key, $i)
        New-Badge $shapes[$i] $lvl.Value $file
        $made++
    }
}
"Создано $made бейджей в $OutDir"
