$ErrorActionPreference = 'Stop'
dotnet build "$PSScriptRoot/WebDavSaveUiRegression.csproj" -p:Platform=x64 -p:NuGetAudit=false --ignore-failed-sources -v:q -clp:ErrorsOnly
if ($LASTEXITCODE -ne 0) { throw 'Save regression build failed.' }
$executable = Join-Path $PSScriptRoot 'bin/x64/Debug/net11.0-windows10.0.26100.0/win-x64/WebDavSaveUiRegression.exe'
$resultPath = Join-Path (Split-Path $executable) 'result.txt'
if (Test-Path $resultPath) { Remove-Item -LiteralPath $resultPath }
$process = Start-Process -FilePath $executable -WindowStyle Hidden -PassThru
if (-not $process.WaitForExit(20000)) {
    Stop-Process -Id $process.Id
    throw 'Save regression timed out.'
}
$result = Get-Content -LiteralPath $resultPath -Raw
Write-Output $result
if ($process.ExitCode -ne 0 -or -not $result.Contains('PASS:')) { throw 'Save regression failed.' }
