param([switch]$Inspect, [switch]$SampleDisplay, [switch]$SampleWindow, [switch]$MoveAcrossDisplays, [string]$PublishedControls, [switch]$CheckMemory, [switch]$CheckHandoff, [switch]$CheckPageCover, [switch]$CheckCoverMotion)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$project = Join-Path $PSScriptRoot 'PlaybackHdrProbe.csproj'
dotnet build $project -c Release -p:Platform=x64 -p:NuGetAudit=false --disable-build-servers -v:q -clp:ErrorsOnly
if ($LASTEXITCODE -ne 0) { throw 'HDR probe build failed' }
$bin = Join-Path $PSScriptRoot 'bin/x64/Release/net11.0-windows10.0.26100.0/win-x64'
if ($PublishedControls) {
    $releaseControls = Get-Item -LiteralPath $PublishedControls
    if ($releaseControls.Name -ne 'AnimatedWin2dControls.dll') { throw 'Expected the published controls assembly' }
    $env:PLAYBACK_HDR_PROBE_CONTROLS_HASH = (Get-FileHash -LiteralPath $releaseControls.FullName).Hash
}
$native = Join-Path $PSScriptRoot 'CompositionProbe.cpp'
if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) {
    $locator = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $vs = & $locator -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    & (Join-Path $vs 'Common7/Tools/Launch-VsDevShell.ps1') -Arch amd64 -HostArch amd64 -SkipAutomaticLocation
}
Push-Location $bin
try {
    cl.exe /nologo /EHsc /std:c++20 /W4 /LD $native /link /OUT:PlaybackHdrComposition.dll d3d11.lib dxgi.lib dcomp.lib dwmapi.lib user32.lib gdi32.lib windowsapp.lib
    if ($LASTEXITCODE -ne 0) { throw 'Native composition probe build failed' }
    if ($PublishedControls) {
        $hostControls = Join-Path $bin $releaseControls.Name
        $originalControls = [IO.File]::ReadAllBytes($hostControls)
        $originalWriteTime = [IO.File]::GetLastWriteTimeUtc($hostControls)
        Copy-Item -LiteralPath $releaseControls.FullName -Destination $hostControls -Force
    }
    $launch = @{ FilePath = (Join-Path $bin 'PlaybackHdrProbe.exe'); PassThru = $true; WindowStyle = 'Hidden'
        RedirectStandardOutput = (Join-Path $bin 'result.txt'); RedirectStandardError = (Join-Path $bin 'errors.txt') }
    if ($CheckCoverMotion) { $launch.ArgumentList = '--check-cover-motion' }
    elseif ($CheckPageCover) { $launch.ArgumentList = '--check-page-cover' }
    elseif ($CheckHandoff) { $launch.ArgumentList = '--check-hdr-handoff' }
    elseif ($CheckMemory) { $launch.ArgumentList = '--check-hdr-memory' }
    elseif ($MoveAcrossDisplays) { $launch.ArgumentList = @('--sample-window', '--move-displays') }
    elseif ($Inspect) { $launch.ArgumentList = '--inspect' }
    elseif ($SampleWindow) { $launch.ArgumentList = '--sample-window' }
    elseif ($SampleDisplay) { $launch.ArgumentList = '--sample' }
    $process = Start-Process @launch
    if (-not $process.WaitForExit(60000)) { $process.Kill(); throw 'HDR probe timed out' }
    Get-Content -LiteralPath $launch.RedirectStandardOutput
    Get-Content -LiteralPath $launch.RedirectStandardError
    if ($process.ExitCode -ne 0) { throw "HDR probe exited with $($process.ExitCode)" }
} finally {
    if ($null -ne $originalControls) {
        [IO.File]::WriteAllBytes($hostControls, $originalControls)
        [IO.File]::SetLastWriteTimeUtc($hostControls, $originalWriteTime)
    }
    Pop-Location
}
