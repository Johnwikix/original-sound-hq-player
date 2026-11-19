param([string]$Source, [string]$Output)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Xml.Linq
$page = [System.Xml.Linq.XDocument]::Load($Source)
$root = $page.Root.Elements() | Select-Object -First 1
$xName = [System.Xml.Linq.XName]::Get('Name', 'http://schemas.microsoft.com/winfx/2006/xaml')

function Find-Node([string]$name) {
    $node = $root.Descendants() | Where-Object { $_.Attribute($xName).Value -eq $name }
    if ($null -eq $node) { throw "Production playing detail XAML is missing $name" }
    $node
}

$background = Find-Node 'NowPlaying'
$layout = Find-Node 'PlayingDetail'
$left = Find-Node 'LeftControlPanel'
$cover = Find-Node 'CoverContainer'
$title = Find-Node 'AnimatedTextBlock'
$lyrics = Find-Node 'LyricsRegionHost'

# Keep the production background/cover bindings and all their layout parents. The
# stand-ins for title and lyrics retain their grid placement for the page layout code.
foreach ($node in @($root.Elements())) {
    if ($node -ne $background -and $node -ne $layout) { $node.Remove() }
}
foreach ($node in @($layout.Elements())) {
    if ($node -ne $left -and $node -ne $lyrics) { $node.Remove() }
}
foreach ($node in @($left.Elements())) {
    if ($node -ne $cover -and $node -ne $title) { $node.Remove() }
}
$title.RemoveNodes()
$lyrics.RemoveNodes()
$lyrics.SetAttributeValue('SizeChanged', $null)
foreach ($attribute in @('SpectrumOptions', 'SpectrumSource', 'EnableLightWave', 'ExceptionOccurred', 'IsFogEnabled', 'IsRaindropEnabled',
    'IsSnowEnabled', 'LyricLineClicked', 'RenderError', 'ThemeResolved', 'UseImageDominantTheme')) {
    $background.SetAttributeValue($attribute, $null)
}

$directory = [IO.Path]::GetDirectoryName($Output)
[IO.Directory]::CreateDirectory($directory) | Out-Null
$settings = [System.Xml.XmlWriterSettings]::new()
$settings.Indent = $true
$settings.NewLineChars = "`r`n"
$writer = [System.Xml.XmlWriter]::Create($Output, $settings)
try { $page.Save($writer) }
finally { $writer.Dispose() }
