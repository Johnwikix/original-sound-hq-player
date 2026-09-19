$ErrorActionPreference = 'Stop'
dotnet build "$PSScriptRoot/AnimatedTextRegression.csproj" -p:Platform=x64 -v:q -clp:ErrorsOnly
if ($LASTEXITCODE -ne 0) { throw 'Regression build failed.' }
$executable = Join-Path $PSScriptRoot 'bin/x64/Debug/net11.0-windows10.0.26100.0/win-x64/AnimatedTextRegression.exe'
$process = Start-Process -FilePath $executable -WindowStyle Hidden -PassThru
if (-not $process.WaitForExit(30000)) {
    Stop-Process -Id $process.Id
    throw 'Regression timed out.'
}
$result = Get-Content (Join-Path (Split-Path $executable) 'result.txt') -Raw
Write-Output $result
if ($process.ExitCode -ne 0 -or -not $result.StartsWith('PASS:')) {
    throw 'AnimatedTextBlock regression failed.'
}
