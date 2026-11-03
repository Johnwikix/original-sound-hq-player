$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
dotnet build "$PSScriptRoot/LocalControlsUiRegression.csproj" -p:Platform=x64 -p:UseSharedCompilation=false --ignore-failed-sources -v:q -clp:ErrorsOnly -nodeReuse:false
if ($LASTEXITCODE -ne 0) { throw 'Local controls build failed' }
$testExe = Join-Path $PSScriptRoot 'bin/x64/Debug/net11.0-windows10.0.26100.0/win-x64/LocalControlsUiRegression.exe'
$resultPath = Join-Path (Split-Path $testExe) 'result.txt'
if (Test-Path -LiteralPath $resultPath) { Remove-Item -LiteralPath $resultPath }
$testProcess = Start-Process -FilePath $testExe -WindowStyle Hidden -PassThru
try {
    if (-not $testProcess.WaitForExit(45000)) { throw 'Local controls regression timed out' }
    $result = Get-Content -LiteralPath $resultPath -Raw
    Write-Output $result
    if ($testProcess.ExitCode -ne 0 -or $result.Contains('FAIL:') -or -not $result.StartsWith('PASS:')) {
        throw 'Local controls regression failed'
    }
} finally {
    if (-not $testProcess.HasExited) { Stop-Process -Id $testProcess.Id }
}
