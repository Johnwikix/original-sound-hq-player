param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$Trace,
    [switch]$InputCapture
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER = '1'
dotnet build "$PSScriptRoot/DrawPathProbe.csproj" -c $Configuration -p:Platform=x64 --disable-build-servers -m:1 -p:NuGetAudit=false -v:q -clp:ErrorsOnly
if ($LASTEXITCODE -ne 0) { throw 'Draw probe build failed.' }
$outputDirectory = Join-Path $PSScriptRoot "bin/x64/$Configuration/net11.0-windows10.0.26100.0/win-x64"
$executable = Join-Path $outputDirectory 'DrawPathProbe.exe'
$resultPath = Join-Path $outputDirectory 'result.json'
if (Test-Path -LiteralPath $resultPath) { Remove-Item -LiteralPath $resultPath }
$probeArguments = @()
if ($InputCapture) { $probeArguments += '--input', '--interactive-input' }
if ($Trace) {
    $traceTool = Join-Path $env:USERPROFILE '.dotnet/tools/dotnet-trace.exe'
    if (-not (Test-Path -LiteralPath $traceTool)) { throw 'Install dotnet-trace before requesting a trace.' }
    $tracePath = Join-Path $outputDirectory 'draw-path.nettrace'
    if (-not $InputCapture) { $probeArguments += '--trace' }
    & $traceTool collect --profile 'gc-verbose,dotnet-sampled-thread-time' --providers 'MusicPlayer-DrawPathProbe:0xFFFFFFFFFFFFFFFF:5' --duration 00:00:00:55 --output $tracePath -- $executable @probeArguments
    if ($LASTEXITCODE -ne 0) { throw 'Draw probe trace or child process failed.' }
} else {
    $startArguments = @{ FilePath = $executable; WindowStyle = 'Hidden'; PassThru = $true }
    if ($probeArguments.Count) { $startArguments.ArgumentList = $probeArguments }
    $process = Start-Process @startArguments
    if (-not $process.WaitForExit(55000)) { Stop-Process -Id $process.Id; throw 'Draw probe timed out.' }
    if ($process.ExitCode -ne 0) { Get-Content -LiteralPath $resultPath; throw 'Draw probe failed.' }
}
$result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
if ($result.Status -ne 'PASS') { throw "Draw probe failed: $($result.Error)" }
$result | ConvertTo-Json -Depth 8
