param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$pluginOutput = Join-Path $repoRoot '_tools/artifacts/plugins/originalsound.lyrics-search'
dotnet publish (Join-Path $repoRoot 'External/Plugins/LyricsSearch/LyricsSearch.csproj') -c $Configuration -p:NuGetAudit=false -o $pluginOutput
if ($LASTEXITCODE -ne 0) { throw 'Lyrics plugin publish failed.' }
$bundledOutput = Join-Path $repoRoot 'BundledPlugins/originalsound.lyrics-search'
New-Item -ItemType Directory -Path $bundledOutput -Force | Out-Null
Get-ChildItem -LiteralPath $pluginOutput -File | Where-Object { $_.Extension -in '.dll', '.json' } |
    Copy-Item -Destination $bundledOutput -Force
Write-Output "Plugin package: $pluginOutput"
Write-Output "Bundled package: $bundledOutput"
Write-Output 'The app seeds the bundled package into Documents/OriginalSoundPlayer/Plugins on startup if absent. Enable it in Settings > Plugins.'
