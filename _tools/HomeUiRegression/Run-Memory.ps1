param([string]$RunName = 'memory-final', [switch]$RetainTick, [switch]$Trace)
$ErrorActionPreference = 'Stop'
$executable = Join-Path $PSScriptRoot 'bin/x64/Debug/net11.0-windows10.0.26100.0/win-x64/HomeUiRegression.exe'
$output = Join-Path (Resolve-Path (Join-Path $PSScriptRoot '../..')) "artifacts/home-polish-batch2/$RunName"
New-Item -ItemType Directory -Path $output -Force | Out-Null
foreach ($flag in @('HOME_POLISH_UI_GC','HOME_POLISH_NO_WEAK','HOME_POLISH_DETACH','HOME_POLISH_RETAIN_TICKS')) { Remove-Item "Env:$flag" -ErrorAction SilentlyContinue }
$env:HOME_POLISH_PROBE = '1'
$env:HOME_POLISH_AUTOMEMORY = '1'
$env:HOME_POLISH_OUTPUT = $output
if ($RetainTick) { $env:HOME_POLISH_RETAIN_TICKS = '1' }
$process = Start-Process -FilePath $executable -WindowStyle Hidden -PassThru
$collector = $null
try {
    if ($Trace) {
        $tracePath = Join-Path $output 'gc.nettrace'
        $collector = Start-Process -FilePath 'C:/Users/90684/.dotnet/tools/dotnet-trace.exe' -ArgumentList @('collect','--process-id', $process.Id, '--providers', 'Microsoft-Windows-DotNETRuntime:0x1:5,HomePolishProbe:0xFFFFFFFFFFFFFFFF:4', '--output', $tracePath, '--duration','00:00:30') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $output 'trace.log') -RedirectStandardError (Join-Path $output 'trace-error.log')
    }
    while (-not $process.WaitForExit(500)) {
        if (([DateTime]::Now - $process.StartTime).TotalSeconds -gt 60) { throw 'Memory fixture timeout' }
    }
    if ($process.ExitCode -ne 0) { throw "Memory fixture exit $($process.ExitCode)" }
    $state = Get-Content (Join-Path $output 'state.json') -Raw | ConvertFrom-Json
    if ($state.Status -ne 'PASS' -or $state.Failures -ne 0 -or $state.ForegroundViolations -ne 0) { throw "Memory fixture failed: $($state.Status)" }
    $state.Samples | Select-Object Phase,CreatedPages,AlivePages,FinalizedPages,PrivateBytes,@{n='GpuShared';e={$_.Gpu.Shared}},@{n='GpuStatus';e={$_.Gpu.Status}} | Format-Table
    if ($collector -and -not $collector.WaitForExit(15000)) { throw 'Trace collector did not finish' }
    if ($collector -and $collector.ExitCode -ne 0) { throw "Trace collector exit $($collector.ExitCode)" }
    Write-Output "PASS: $RunName; checks=$($state.Checks.Count), foreground=$($state.ForegroundViolations)"
} finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id }
    if ($collector -and -not $collector.HasExited) { Stop-Process -Id $collector.Id }
}
