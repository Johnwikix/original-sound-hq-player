$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
dotnet build "$PSScriptRoot/FrameAbiRegression.csproj" -p:Platform=x64 --disable-build-servers -v:q -clp:ErrorsOnly
if ($LASTEXITCODE -ne 0) { throw 'Frame ABI regression build failed.' }
$executable = Join-Path $PSScriptRoot 'bin/x64/Debug/net11.0-windows10.0.26100.0/win-x64/FrameAbiRegression.exe'
$process = Start-Process -FilePath $executable -WindowStyle Hidden -PassThru
if (-not $process.WaitForExit(45000)) {
    Stop-Process -Id $process.Id
    throw 'Frame ABI regression timed out.'
}
$result = Get-Content (Join-Path (Split-Path $executable) 'result.txt') -Raw
Write-Output $result
if ($process.ExitCode -ne 0 -or -not $result.StartsWith('PASS:')) {
    throw 'Frame ABI regression failed.'
}
