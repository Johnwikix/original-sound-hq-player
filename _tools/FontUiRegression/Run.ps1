$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
python "$PSScriptRoot/GenerateFixtures.py"
if ($LASTEXITCODE -ne 0) { throw 'Font fixture generation failed' }
dotnet build "$PSScriptRoot/FontUiRegression.csproj" -p:Platform=x64 -v:q -clp:ErrorsOnly -nodeReuse:false --ignore-failed-sources
if ($LASTEXITCODE -ne 0) { throw 'Font regression build failed' }
$testExe = Join-Path $PSScriptRoot 'bin/x64/Debug/net11.0-windows10.0.26100.0/win-x64/FontUiRegression.exe'
$resultPath = Join-Path (Split-Path $testExe) 'result.txt'
if (Test-Path -LiteralPath $resultPath) { Remove-Item -LiteralPath $resultPath }
$testProcess = Start-Process -FilePath $testExe -WindowStyle Hidden -PassThru
if (-not $testProcess.WaitForExit(30000)) {
    Stop-Process -Id $testProcess.Id
    throw 'Font regression timed out'
}
$result = Get-Content -LiteralPath $resultPath -Raw
Write-Output $result
if ($testProcess.ExitCode -ne 0 -or -not $result.StartsWith('PASS:')) { throw 'Font regression failed' }
