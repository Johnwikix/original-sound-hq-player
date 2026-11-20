param([string] $NativePlayer)
$ErrorActionPreference = 'Stop'
dotnet build "$PSScriptRoot/PlaybackNavigationUiRegression.csproj" --no-restore -p:Platform=x64 -v:q -clp:ErrorsOnly
if ($LASTEXITCODE -ne 0) { throw 'WinUI regression build failed' }
$testExe = Join-Path $PSScriptRoot 'bin/x64/Debug/net11.0-windows10.0.26100.0/win-x64/PlaybackNavigationUiRegression.exe'
$resultPath = Join-Path (Split-Path $testExe) 'result.txt'
if (Test-Path -LiteralPath $resultPath) { Remove-Item -LiteralPath $resultPath }
$launch = @{ FilePath = $testExe; WindowStyle = 'Hidden'; PassThru = $true }
if ($NativePlayer) { $launch.Environment = @{ RAPID_NEXT_TEST_PLAYER = (Resolve-Path -LiteralPath $NativePlayer).Path } }
$testProcess = Start-Process @launch
if (-not $testProcess.WaitForExit(55000)) {
    Stop-Process -Id $testProcess.Id
    throw 'WinUI regression timed out'
}
$result = Get-Content -LiteralPath $resultPath -Raw
Write-Output $result
if ($testProcess.ExitCode -ne 0 -or -not $result.StartsWith('PASS:')) { throw 'WinUI regression failed' }
