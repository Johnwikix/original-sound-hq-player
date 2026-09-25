param([switch]$WithoutTemplate)
$ErrorActionPreference = 'Stop'
dotnet build "$PSScriptRoot/WebDavTreeUiRegression.csproj" -p:Platform=x64 -p:NuGetAudit=false --ignore-failed-sources -v:q -clp:ErrorsOnly
if ($LASTEXITCODE -ne 0) { throw 'UI regression build failed.' }
$executable = Join-Path $PSScriptRoot 'bin/x64/Debug/net11.0-windows10.0.26100.0/win-x64/WebDavTreeUiRegression.exe'
$resultPath = Join-Path (Split-Path $executable) 'result.txt'
if (Test-Path $resultPath) { Remove-Item -LiteralPath $resultPath }
$launch = @{ FilePath = $executable; WindowStyle = 'Hidden'; PassThru = $true }
if ($WithoutTemplate) { $launch.ArgumentList = '--without-template' }
$process = Start-Process @launch
if (-not $process.WaitForExit(30000)) {
    Stop-Process -Id $process.Id
    throw 'UI regression timed out.'
}
if (-not (Test-Path $resultPath)) { throw "UI regression exited before startup: $($process.ExitCode)" }
$result = Get-Content $resultPath -Raw
Write-Output $result
if ($process.ExitCode -ne 0 -or -not $result.StartsWith('PASS:')) { throw 'WebDAV TreeView UI regression failed.' }
