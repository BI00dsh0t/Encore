param([string]$OutputDirectory = $PSScriptRoot)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
$culture = [Globalization.CultureInfo]::InvariantCulture
$mint = '#2DD4BF'
$ink = '#0A0B0E'
$paper = '#E6E8EB'
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
    $context.DrawRoundedRectangle((Brush $ink), $null,
        [Windows.Rect]::new($size / 64, $size / 64, $size * 62 / 64, $size * 62 / 64), $size * 54 / 256, $size * 54 / 256)
    Draw-Mark $context 0 0 $size $mint
    $context.Close()
    Save-Png $visual $size $size "encore-app-$size.png"
}

# ICO frames use lossless PNG data and include the Windows tray and taskbar sizes.
$iconSizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = @($iconSizes | ForEach-Object { ,([IO.File]::ReadAllBytes((Join-Path $OutputDirectory "encore-app-$_.png"))) })
$writer = [IO.BinaryWriter]::new([IO.File]::Create((Join-Path $OutputDirectory 'encore.ico')))
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    for ($index = 0; $index -lt $frames.Count; $index++) {
        $dimension = $iconSizes[$index] % 256
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$index].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$index].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
} finally { $writer.Dispose() }

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
Write-Output "Encore assets generated in $OutputDirectory"
