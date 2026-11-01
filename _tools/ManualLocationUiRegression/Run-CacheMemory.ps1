param([switch]$SkipBuild, [string]$RunName = 'after')
$ErrorActionPreference = 'Stop'
if (-not $SkipBuild) {
    dotnet build "$PSScriptRoot/ManualLocationUiRegression.csproj" -p:Platform=x64 --no-restore -v:q -clp:ErrorsOnly
    if ($LASTEXITCODE -ne 0) { throw 'Cache fixture build failed' }
}
$env:FRAME_CACHE_MEMORY = '1'
$env:FRAME_CACHE_RUN = $RunName
$fixture = Join-Path $PSScriptRoot 'bin/x64/Debug/net11.0-windows10.0.26100.0/win-x64/ManualLocationUiRegression.exe'
$process = Start-Process -FilePath $fixture -WindowStyle Hidden -PassThru
try {
    while (-not $process.WaitForExit(500)) {
        if (([DateTime]::Now - $process.StartTime).TotalSeconds -gt 60) { throw 'Cache fixture timeout' }
    }
    if ($process.ExitCode -ne 0) { throw "Cache fixture exit $($process.ExitCode)" }
    $output = Join-Path (Resolve-Path (Join-Path $PSScriptRoot '../..')) "artifacts/frame-cache/$RunName/memory.json"
    $result = Get-Content -LiteralPath $output -Raw | ConvertFrom-Json
    if ($RunName.Contains('after') -and ($result.Alive -ne 0 -or $result.Created -ne $result.Finalized)) { throw 'Retired pages are still alive' }
    if ($result.ForegroundViolations -ne 0) { throw 'Fixture became foreground' }
    $result | ConvertTo-Json -Depth 4
} finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id }
}
