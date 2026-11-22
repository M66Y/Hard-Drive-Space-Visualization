param([Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$destination = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $destination) { throw 'Preview output already exists.' }
Add-Type -AssemblyName System.Drawing
function Convert-PreviewImage([string]$RelativePath, [int]$MaxSide, [bool]$Transparent) {
    $source = [Drawing.Image]::FromFile((Join-Path $projectRoot $RelativePath))
    $bitmap = $null; $graphics = $null; $stream = $null
    try {
        $ratio = [Math]::Min(1.0, $MaxSide / [double][Math]::Max($source.Width, $source.Height))
        $bitmap = [Drawing.Bitmap]::new([Math]::Max(1,[int]($source.Width*$ratio)), [Math]::Max(1,[int]($source.Height*$ratio)))
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.DrawImage($source, [Drawing.Rectangle]::new(0,0,$bitmap.Width,$bitmap.Height))
        $graphics.Dispose(); $graphics = $null
        $stream = [IO.MemoryStream]::new()
        if ($Transparent) { $bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png); $mime = 'png' }
        else { $bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Jpeg); $mime = 'jpeg' }
        return ('data:image/'+$mime+';base64,'+[Convert]::ToBase64String($stream.ToArray()))
    } finally {
        if ($graphics) { $graphics.Dispose() }; if ($stream) { $stream.Dispose() }
        if ($bitmap) { $bitmap.Dispose() }; $source.Dispose()
    }
}
$template = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'preview-template.html'))
$fragment = $template.Replace('__AL3_WALL__',(Convert-PreviewImage 'assets/background.png' 1440 $false)).Replace('__AL3_ICON__',(Convert-PreviewImage 'assets/app-icon.png' 72 $true))
$encoding = [Text.UTF8Encoding]::new($false)
if ($encoding.GetByteCount($fragment) -ge 1000000) { throw 'Preview exceeds size budget.' }
[IO.File]::WriteAllText($destination,$fragment,$encoding)
Write-Output 'PASS preview generated from public repository assets; no application settings changed.'
