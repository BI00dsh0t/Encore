param([string]$OutputDirectory = $PSScriptRoot)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$culture = [Globalization.CultureInfo]::InvariantCulture
$mint = '#2DD4BF'
$ink = '#0A0B0E'
$paper = '#E6E8EB'
$record = '#FF4655'
$caution = '#F5B83D'
# The 300-degree ring follows an exact circle centered at 128,128, with radius 82.
$arc = 'M 56.985917,87 A 82,82 0 1 0 128,46'
# A vertical base centers the left-pointing arrow on the horizontal shaft at y=46.
$head = 'M 90,46 L 128,22 L 128,70 Z'
$geometry = @"
<g transform="rotate(-45 128 128)">
<path d="$arc" fill="none" stroke="currentColor" stroke-width="28" stroke-linecap="butt"/>
<circle cx="56.985917" cy="87" r="14" fill="currentColor"/>
<path d="$head" fill="currentColor"/>
</g>
"@

function Write-Svg([string]$Name, [string]$ViewBox, [string]$Body) {
    $svg = "<svg xmlns=`"http://www.w3.org/2000/svg`" viewBox=`"$ViewBox`" role=`"img`"><title>Encore</title>$Body</svg>"
    [IO.File]::WriteAllText((Join-Path $OutputDirectory $Name), $svg)
}

function Brush([string]$Color) {
    return [Windows.Media.BrushConverter]::new().ConvertFromString($Color)
}

function Draw-Mark($Context, [double]$X, [double]$Y, [double]$Size, [string]$Color) {
    $Context.PushTransform([Windows.Media.TranslateTransform]::new($X, $Y))
    $Context.PushTransform([Windows.Media.ScaleTransform]::new($Size / 256, $Size / 256))
    $Context.PushTransform([Windows.Media.RotateTransform]::new(-45, 128, 128))
    $fill = Brush $Color
    $pen = [Windows.Media.Pen]::new($fill, 28)
    $pen.StartLineCap = [Windows.Media.PenLineCap]::Round
    $pen.EndLineCap = [Windows.Media.PenLineCap]::Flat
    $Context.DrawGeometry($null, $pen, [Windows.Media.Geometry]::Parse($arc))
    $Context.DrawGeometry($fill, $null, [Windows.Media.Geometry]::Parse($head))
    $Context.Pop()
    $Context.Pop()
    $Context.Pop()
}

function Text-Shape([string]$Text, [double]$Size, [string]$Weight = 'Normal') {
    $face = [Windows.Media.Typeface]::new(
        [Windows.Media.FontFamily]::new('Segoe UI'), [Windows.FontStyles]::Normal,
        [Windows.FontWeightConverter]::new().ConvertFromString($Weight), [Windows.FontStretches]::Normal)
    return [Windows.Media.FormattedText]::new($Text, $culture, [Windows.FlowDirection]::LeftToRight,
        $face, $Size, [Windows.Media.Brushes]::Black, 1.0)
}

function Draw-Text($Context, [string]$Text, [double]$X, [double]$Y, [double]$Size, [string]$Color, [string]$Weight = 'Normal') {
    $shape = Text-Shape $Text $Size $Weight
    $shape.SetForegroundBrush((Brush $Color))
    $Context.DrawText($shape, [Windows.Point]::new($X, $Y))
}

function Save-Png($Visual, [int]$Width, [int]$Height, [string]$Name) {
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($Width, $Height, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($Visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.File]::Create((Join-Path $OutputDirectory $Name))
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
}

function Draw-Tile($Context, [double]$Size) {
    $Context.DrawRoundedRectangle((Brush $ink), $null,
        [Windows.Rect]::new($Size / 64, $Size / 64, $Size * 62 / 64, $Size * 62 / 64), $Size * 54 / 256, $Size * 54 / 256)
    Draw-Mark $Context 0 0 $Size $mint
}

function Render-Frame([int]$Size, [scriptblock]$Draw) {
    $visual = [Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    & $Draw $context $Size
    $context.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($Size, $Size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    return $bitmap
}

function Png-Bytes($Bitmap) {
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($Bitmap))
    $stream = [IO.MemoryStream]::new()
    $encoder.Save($stream)
    return ,$stream.ToArray()
}

# A DIB frame is a BITMAPINFOHEADER, straight-alpha BGRA rows bottom-up, then the transparency mask.
function Dib-Bytes($Bitmap, [int]$Size) {
    $pixels = [byte[]]::new($Size * $Size * 4)
    $Bitmap.CopyPixels($pixels, $Size * 4, 0)
    $maskStride = [Math]::Floor(($Size + 31) / 32) * 4
    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream)
    $writer.Write([uint32]40); $writer.Write([int32]$Size); $writer.Write([int32]($Size * 2))
    $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]0)
    $writer.Write([uint32]($Size * $Size * 4 + $maskStride * $Size))
    $writer.Write([int32]0); $writer.Write([int32]0); $writer.Write([uint32]0); $writer.Write([uint32]0)
    $mask = [byte[]]::new($maskStride * $Size)
    for ($row = $Size - 1; $row -ge 0; $row--) {
        for ($column = 0; $column -lt $Size; $column++) {
            Write-DibPixel $writer $pixels (($row * $Size + $column) * 4)
            if ($pixels[($row * $Size + $column) * 4 + 3] -eq 0) {
                $maskIndex = ($Size - 1 - $row) * $maskStride + [Math]::Floor($column / 8)
                $mask[$maskIndex] = $mask[$maskIndex] -bor (0x80 -shr ($column % 8))
            }
        }
    }
    $writer.Write($mask)
    $writer.Flush()
    return ,$stream.ToArray()
}

# WPF renders premultiplied alpha; an icon's DIB wants it straight.
function Write-DibPixel($Writer, [byte[]]$Pixels, [int]$Index) {
    $alpha = $Pixels[$Index + 3]
    for ($channel = 0; $channel -lt 3; $channel++) {
        $value = 0
        if ($alpha -gt 0) { $value = [Math]::Min(255, [Math]::Round($Pixels[$Index + $channel] * 255.0 / $alpha)) }
        $Writer.Write([byte]$value)
    }
    $Writer.Write([byte]$alpha)
}

function Write-Icon([string]$Path, [int[]]$Sizes, [scriptblock]$Draw) {
    $frames = [Collections.Generic.List[byte[]]]::new()
    foreach ($size in $Sizes) {
        $bitmap = Render-Frame $size $Draw
        if ($size -ge 256) { $frames.Add((Png-Bytes $bitmap)) } else { $frames.Add((Dib-Bytes $bitmap $size)) }
    }
    $writer = [IO.BinaryWriter]::new([IO.File]::Create($Path))
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
        $offset = 6 + 16 * $frames.Count
        for ($index = 0; $index -lt $frames.Count; $index++) {
            $dimension = $Sizes[$index] % 256
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$index].Length); $writer.Write([uint32]$offset)
            $offset += $frames[$index].Length
        }
        foreach ($frame in $frames) { $writer.Write($frame) }
    } finally { $writer.Dispose() }
}

# A corner badge sits on whole pixels in the bottom-right, with a transparent gap cut around it.
function Draw-Badged($Context, [int]$Size, [double]$Share, [scriptblock]$Base, [scriptblock]$Badge) {
    $radius = [Math]::Round($Size * $Share)
    $center = [Windows.Point]::new($Size - $radius, $Size - $radius)
    $gap = [Math]::Max(1, $Size / 16)
    $cut = [Windows.Media.EllipseGeometry]::new($center, $radius + $gap, $radius + $gap)
    $whole = [Windows.Media.RectangleGeometry]::new([Windows.Rect]::new(0, 0, $Size, $Size))
    $Context.PushClip([Windows.Media.CombinedGeometry]::new([Windows.Media.GeometryCombineMode]::Exclude, $whole, $cut))
    & $Base $Context $Size
    $Context.Pop()
    & $Badge $Context $center $radius
}

function Draw-Caution($Context, [Windows.Point]$Center, [double]$Radius) {
    $Context.DrawEllipse((Brush $caution), $null, $Center, $Radius, $Radius)
    $width = [Math]::Max(1.2, $Radius * 0.34)
    $bar = [Windows.Rect]::new($Center.X - $width / 2, $Center.Y - $Radius * 0.62, $width, $Radius * 0.8)
    $Context.DrawRoundedRectangle((Brush $ink), $null, $bar, $width / 2, $width / 2)
    $dot = [Windows.Point]::new($Center.X, $Center.Y + $Radius * 0.5)
    $Context.DrawEllipse((Brush $ink), $null, $dot, $width * 0.6, $width * 0.6)
}

function Draw-RecordDot($Context, [Windows.Point]$Center, [double]$Radius) {
    $Context.DrawEllipse((Brush $record), $null, $Center, $Radius, $Radius)
}

# Idle and error use a one-colour mark per taskbar theme; capturing shows the app tile, which reads on either.
function Write-TrayIcons([string]$Directory) {
    $traySizes = @(16, 20, 24, 32, 40, 48)
    foreach ($variant in @(@('dark', $paper), @('light', $ink))) {
        $color = $variant[1]
        $mark = { param($Context, $Size) Draw-Mark $Context 0 0 $Size $color }.GetNewClosure()
        Write-Icon (Join-Path $Directory "tray-idle-$($variant[0]).ico") $traySizes $mark
        $badged = { param($Context, $Size) Draw-Badged $Context $Size 0.25 $mark ${function:Draw-Caution} }.GetNewClosure()
        Write-Icon (Join-Path $Directory "tray-error-$($variant[0]).ico") $traySizes $badged
    }
    $tile = { param($Context, $Size) Draw-Tile $Context $Size }
    Write-Icon (Join-Path $Directory 'tray-ready.ico') $traySizes $tile
    $recording = { param($Context, $Size) Draw-Badged $Context $Size 0.22 $tile ${function:Draw-RecordDot} }.GetNewClosure()
    Write-Icon (Join-Path $Directory 'tray-recording.ico') $traySizes $recording
}

# GitHub's social preview is 1280 by 640; it is uploaded by hand, so it stays with the other exports.
function Write-SocialPreview {
    $visual = [Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    $context.DrawRectangle((Brush $ink), $null, [Windows.Rect]::new(0, 0, 1280, 640))
    Draw-Mark $context 130 170 300 $mint
    Draw-Text $context 'Encore' 470 180 140 $paper 'SemiBold'
    Draw-Text $context 'Keep the moment.' 478 372 40 $mint
    Draw-Text $context 'Instant replay and recording for Windows.' 478 432 30 '#8B949E'
    $context.Close()
    Save-Png $visual 1280 640 'encore-social-preview.png'
}

Write-Svg 'encore-mark.svg' '0 0 256 256' "<g color=`"$mint`">$geometry</g>"
Write-Svg 'encore-mark-white.svg' '0 0 256 256' "<g color=`"$paper`">$geometry</g>"
Write-Svg 'encore-mark-ink.svg' '0 0 256 256' "<g color=`"$ink`">$geometry</g>"
$tile = "<rect x=`"4`" y=`"4`" width=`"248`" height=`"248`" rx=`"54`" fill=`"$ink`"/>"
Write-Svg 'encore-app-icon.svg' '0 0 256 256' "$tile<g color=`"$mint`">$geometry</g>"
$wordGeometry = (Text-Shape 'Encore' 76 'SemiBold').BuildGeometry([Windows.Point]::new(0, 0))
$wordShape = $wordGeometry.GetOutlinedPathGeometry().ToString($culture)
foreach ($variant in @(@('dark', $paper), @('light', $ink))) {
    $body = "<g transform=`"translate(0 2) scale(.5)`" color=`"$mint`">$geometry</g>"
    $body += "<path transform=`"translate(144 11)`" fill=`"$($variant[1])`" d=`"$wordShape`"/>"
    Write-Svg "encore-lockup-$($variant[0]).svg" '0 0 440 132' $body
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256, 512, 1024)
foreach ($size in $sizes) {
    $visual = [Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    Draw-Mark $context 0 0 $size $mint
    $context.Close()
    Save-Png $visual $size $size "encore-mark-$size.png"
    $visual = [Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    Draw-Tile $context $size
    $context.Close()
    Save-Png $visual $size $size "encore-app-$size.png"
}

# The Windows tray and taskbar sizes; 256 px stays PNG, smaller frames are DIBs.
$iconSizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
Write-Icon (Join-Path $OutputDirectory 'encore.ico') $iconSizes { param($Context, $Size) Draw-Tile $Context $Size }

$iconDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\src\Encore.App\Icons'))
Copy-Item (Join-Path $OutputDirectory 'encore.ico') (Join-Path $iconDirectory 'encore.ico')
Write-TrayIcons $iconDirectory

$visual = [Windows.Media.DrawingVisual]::new()
$context = $visual.RenderOpen()
$context.DrawRectangle((Brush $ink), $null, [Windows.Rect]::new(0, 0, 1200, 760))
Draw-Text $context 'ENCORE / BRAND IDENTITY' 60 38 13 '#8B949E' 'SemiBold'
Draw-Mark $context 43 111 228 $mint
Draw-Text $context 'Encore' 285 137 100 $paper 'SemiBold'
Draw-Text $context 'Keep the moment.' 292 260 26 '#8B949E'
$context.DrawLine([Windows.Media.Pen]::new((Brush '#262B34'), 1), [Windows.Point]::new(60, 366), [Windows.Point]::new(1140, 366))
Draw-Text $context '01 / THE MARK' 60 402 12 '#8B949E' 'SemiBold'
Draw-Mark $context 52 447 120 $mint
Draw-Mark $context 190 460 88 $paper
Draw-Text $context 'A circular replay arrow.' 60 595 19 $paper
Draw-Text $context 'One circle. Constant weight. Room to breathe.' 60 630 15 '#8B949E'
Draw-Text $context '02 / APP ICON' 430 402 12 '#8B949E' 'SemiBold'
$context.DrawRoundedRectangle((Brush '#16191F'), [Windows.Media.Pen]::new((Brush '#262B34'), 1), [Windows.Rect]::new(430, 455, 112, 112), 24, 24)
Draw-Mark $context 430 455 112 $mint
foreach ($sample in @(@(572, 48), @(650, 32), @(714, 16))) { Draw-Mark $context $sample[0] (511 - $sample[1] / 2) $sample[1] $mint }
Draw-Text $context 'Built for the desktop.' 430 595 19 $paper
Draw-Text $context '16 px tray to 1024 px master.' 430 630 15 '#8B949E'
Draw-Text $context '03 / PALETTE' 820 402 12 '#8B949E' 'SemiBold'
$colors = @(@($mint, 'Mint', '#2DD4BF'), @('#16191F', 'Surface', '#16191F'), @($paper, 'Mist', '#E6E8EB'))
for ($index = 0; $index -lt $colors.Count; $index++) {
    $x = 820 + 110 * $index
    $context.DrawRoundedRectangle((Brush $colors[$index][0]), $null, [Windows.Rect]::new($x, 463, 88, 88), 12, 12)
    Draw-Text $context $colors[$index][1] $x 565 15 $paper
    Draw-Text $context $colors[$index][2] $x 593 12 '#8B949E'
}
Draw-Text $context 'WINDOWS REPLAY & RECORDING' 60 706 12 '#8B949E' 'SemiBold'
Draw-Text $context 'Exact circular geometry / Segoe UI Semibold' 820 706 12 '#8B949E'
$context.Close()
Save-Png $visual 1200 760 'encore-brand-preview.png'
Write-SocialPreview
Write-Output "Encore assets generated in $OutputDirectory and $iconDirectory"
