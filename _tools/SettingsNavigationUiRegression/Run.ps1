$ErrorActionPreference = 'Stop'
dotnet build "$PSScriptRoot/SettingsNavigationUiRegression.csproj" -p:Platform=x64 -v:q -clp:ErrorsOnly -nodeReuse:false
if ($LASTEXITCODE -ne 0) { throw 'Settings navigation regression build failed' }
$testExe = Join-Path $PSScriptRoot 'bin/x64/Debug/net11.0-windows10.0.26100.0/win-x64/SettingsNavigationUiRegression.exe'
$resultPath = Join-Path (Split-Path $testExe) 'result.txt'
if (Test-Path -LiteralPath $resultPath) { Remove-Item -LiteralPath $resultPath }
$testProcess = Start-Process -FilePath $testExe -WindowStyle Hidden -PassThru
if (-not $testProcess.WaitForExit(30000)) {
    Stop-Process -Id $testProcess.Id
    throw 'Settings navigation regression timed out'
}
$result = Get-Content -LiteralPath $resultPath -Raw
Write-Output $result
if ($testProcess.ExitCode -ne 0 -or $result.Contains('FAIL:')) { throw 'Settings navigation regression failed' }
