# Generate website SVG and multi-resolution Windows ICO from the existing APP sidebar badge.
# No APP build, release installation, website staging or deployment is performed here.
param([string]$WebsiteRoot='D:\DWGC2E_Website',[string]$EvidenceDirectory='')
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationCore,PresentationFramework,WindowsBase
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if(!$EvidenceDirectory){$EvidenceDirectory=Join-Path $root 'artifacts/brand-unification-20260916'}
[IO.Directory]::CreateDirectory($EvidenceDirectory)|Out-Null
[xml]$window=Get-Content -LiteralPath (Join-Path $root 'src/DwgTranslator.App/Views/MainWindow.xaml') -Raw -Encoding UTF8
[xml]$tokens=Get-Content -LiteralPath (Join-Path $root 'src/DwgTranslator.App/Themes/ColorTokens.xaml') -Raw -Encoding UTF8
# Anchor on the badge structure, not on the letter: the letters change with the product name
# (DWG/DWGC2E -> QLCAD took the badge from "D" to "Q"), and a hard-coded letter would either throw
# or silently regenerate the wrong logo. SidebarBrand is the single source of truth for the badge.
$brand=$window.SelectSingleNode('//*[local-name()="StackPanel" and @*[local-name()="Name"]="SidebarBrand"]')
if(!$brand){throw 'APP sidebar brand block not found; refusing to invent a logo'}
$label=$brand.SelectSingleNode('.//*[local-name()="TextBlock"]')
if(!$label){throw 'APP sidebar badge not found; refusing to invent a logo'}
Write-Host "BRAND_BADGE=$($label.Text)"
$badge=$label.ParentNode
$color=$tokens.SelectSingleNode('//*[local-name()="SolidColorBrush" and @*[local-name()="Key"]="Brush.Primary"]').Color
$radius=[double]$tokens.SelectSingleNode('//*[local-name()="CornerRadius" and @*[local-name()="Key"]="CornerRadius.Medium"]').TopLeft
$size=[double]$badge.Width
if($badge.Width -ne $badge.Height -or $label.FontWeight -ne 'Bold'){throw 'Unsupported APP badge layout'}
$font=[Windows.Media.FontFamily]::new($window.DocumentElement.FontFamily)
$typeface=[Windows.Media.Typeface]::new($font,[Windows.FontStyles]::Normal,[Windows.FontWeights]::Bold,[Windows.FontStretches]::Normal)
$text=[Windows.Media.FormattedText]::new($label.Text,[Globalization.CultureInfo]::InvariantCulture,[Windows.FlowDirection]::LeftToRight,$typeface,[double]$label.FontSize,[Windows.Media.Brushes]::White,1.0)
$origin=[Windows.Point]::new(($size-$text.Width)/2,($size-$text.Height)/2)
$glyph=$text.BuildGeometry($origin)
$path=$glyph.GetOutlinedPathGeometry().ToString([Globalization.CultureInfo]::InvariantCulture)
$fillRule='evenodd'
if($path.StartsWith('F1')){$fillRule='nonzero';$path=$path.Substring(2).Trim()}
elseif($path.StartsWith('F0')){$path=$path.Substring(2).Trim()}
$svg="<svg xmlns=`"http://www.w3.org/2000/svg`" viewBox=`"0 0 $size $size`" fill=`"none`"><rect width=`"$size`" height=`"$size`" rx=`"$radius`" fill=`"$color`"/><path fill=`"#FFFFFF`" fill-rule=`"$fillRule`" d=`"$path`"/></svg>`n"
$utf8=[Text.UTF8Encoding]::new($false)
foreach($dest in @((Join-Path $root 'assets/icons/brand.svg'),(Join-Path $WebsiteRoot 'assets/logo.svg'),(Join-Path $WebsiteRoot 'favicon.svg'))){[IO.File]::WriteAllText($dest,$svg,$utf8)}
$frames=@()
foreach($pixels in @(16,20,24,32,40,48,64,128,256)){
    $visual=[Windows.Media.DrawingVisual]::new();$dc=$visual.RenderOpen()
    $dc.PushTransform([Windows.Media.ScaleTransform]::new($pixels/$size,$pixels/$size))
    $brush=[Windows.Media.BrushConverter]::new().ConvertFromString($color)
    $dc.DrawRoundedRectangle($brush,$null,[Windows.Rect]::new(0,0,$size,$size),$radius,$radius)
    $dc.DrawGeometry([Windows.Media.Brushes]::White,$null,$glyph)
    $dc.Pop();$dc.Close()
    $bitmap=[Windows.Media.Imaging.RenderTargetBitmap]::new($pixels,$pixels,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new();$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream=[IO.MemoryStream]::new();$encoder.Save($stream);$bytes=$stream.ToArray();$stream.Dispose()
    $frames+=,@{Size=$pixels;Bytes=$bytes}
    if($pixels -eq 256){[IO.File]::WriteAllBytes((Join-Path $EvidenceDirectory 'brand-preview.png'),$bytes)}
}
$ico=[IO.MemoryStream]::new();$writer=[IO.BinaryWriter]::new($ico)
$writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$frames.Count)
$offset=6+16*$frames.Count
foreach($frame in $frames){
    $dimension=if($frame.Size -eq 256){0}else{$frame.Size}
    $writer.Write([byte]$dimension);$writer.Write([byte]$dimension);$writer.Write([byte]0);$writer.Write([byte]0)
    $writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$frame.Bytes.Length);$writer.Write([uint32]$offset)
    $offset+=$frame.Bytes.Length
}
foreach($frame in $frames){$writer.Write([byte[]]$frame.Bytes)}
$writer.Flush();[IO.File]::WriteAllBytes((Join-Path $root 'assets/icons/icon.ico'),$ico.ToArray());$writer.Dispose();$ico.Dispose()
@{Source='APP sidebar badge';Color=$color;Radius=$radius;Font=$font.Source;FontSize=$label.FontSize;Sizes=@($frames|ForEach-Object{$_.Size});Scope='Assets only; no release or website deployment'}|ConvertTo-Json|Set-Content (Join-Path $EvidenceDirectory 'generation.json')
Write-Host 'Brand SVG and nine-resolution ICO generated from APP badge.'
