$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$output = Join-Path $PSScriptRoot 'obj'
New-Item -ItemType Directory -Force -Path $output | Out-Null
Push-Location $repository
try {
    # Last unchanged implementation, before the System.Text.Json migration.
    git archive --format=zip --output="$output/original.zip" caa356c493d1823c7d165b2d4adff139c69b8f7d External/Lyricify.Lyrics.Helper
    if ($LASTEXITCODE -ne 0) { throw 'Could not export the original library.' }
    Expand-Archive -LiteralPath "$output/original.zip" -DestinationPath "$output/source" -Force
    dotnet run --project "$PSScriptRoot/BaselineGenerator" -c Release -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw 'Baseline generation failed.' }
} finally {
    Pop-Location
}
