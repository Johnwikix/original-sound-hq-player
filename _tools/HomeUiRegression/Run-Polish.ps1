param([switch]$SkipBuild, [switch]$Baseline, [string]$RunName = 'after', [switch]$CaptureDump, [string[]]$OnlyActions)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
if (-not $SkipBuild) {
    dotnet build "$PSScriptRoot/HomeUiRegression.csproj" -p:Platform=x64 --no-restore -v:q -clp:ErrorsOnly
    if ($LASTEXITCODE -ne 0) { throw 'Home polish fixture build failed' }
}
$executable = Join-Path $PSScriptRoot 'bin/x64/Debug/net11.0-windows10.0.26100.0/win-x64/HomeUiRegression.exe'
$output = Join-Path (Resolve-Path (Join-Path $PSScriptRoot '../..')) "artifacts/home-polish-batch2/$RunName"
foreach ($flag in @('HOME_POLISH_AUTOMEMORY','HOME_POLISH_UI_GC','HOME_POLISH_NO_WEAK','HOME_POLISH_DETACH','HOME_POLISH_RETAIN_TICKS')) { Remove-Item "Env:$flag" -ErrorAction SilentlyContinue }
$env:HOME_POLISH_PROBE = '1'
$env:HOME_POLISH_OUTPUT = $output
New-Item -ItemType Directory -Path $output -Force | Out-Null
$statePath = Join-Path $output 'state.json'
if (Test-Path -LiteralPath $statePath) { Remove-Item -LiteralPath $statePath }
$process = Start-Process -FilePath $executable -WindowStyle Hidden -PassThru
$script:index = 0
function Wait-State([string]$Action, [int]$Sequence) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt 50) {
        if (Test-Path -LiteralPath $statePath) {
            try { $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json } catch { $state = $null }
            if ($state -and -not $state.Busy -and $state.Action -eq $Action -and $state.Sequence -gt $Sequence) {
                if ($state.Status -ne 'PASS' -and -not $Baseline) { throw "Fixture failed: $($state.Status)" }
                if ($state.ForegroundViolations -ne 0) { throw 'Fixture became foreground' }
                return $state
            }
        }
        if ($process.HasExited) { throw "Fixture exited: $($process.ExitCode)" }
        Start-Sleep -Milliseconds 100
    }
    throw "Timed out waiting for $Action"
}
function Find-Element($Node, [string]$Id) {
    if ($null -eq $Node -or $Node -is [string] -or $Node -is [ValueType]) { return $null }
    if ($Node -is [System.Collections.IEnumerable] -and $Node -isnot [pscustomobject]) {
        foreach ($child in $Node) { $found = Find-Element $child $Id; if ($found) { return $found } }
    } else {
        if ($Node.AutomationId -eq $Id -or $Node.Properties.AutomationId -eq $Id) { return $Node }
        foreach ($property in $Node.PSObject.Properties) { $found = Find-Element $property.Value $Id; if ($found) { return $found } }
    }
    return $null
}
function Invoke-Button([string]$Id) {
    $script:index++
    $prefix = '{0:D2}-{1}' -f $script:index, $Id
    $tree = & 'G:/Tool/lvt/lvt.exe' dump --hwnd $script:hwnd --uia --depth 9 --uia-timeout 5000
    if ($LASTEXITCODE -ne 0) { throw 'lvt dump failed' }
    [IO.File]::WriteAllText((Join-Path $output "$prefix-tree.json"), ($tree | Out-String).ReplaceLineEndings("`r`n"))
    $element = Find-Element (($tree | Out-String) | ConvertFrom-Json) $Id
    if (-not $element) { throw "AutomationId $Id missing" }
    $reference = if ($element.Properties.RuntimeId) { 'uia:' + $element.Properties.RuntimeId } else { $element.Id }
    $result = & 'G:/Tool/lvt/lvt.exe' invoke $reference --hwnd $script:hwnd
    if ($LASTEXITCODE -ne 0) { throw "lvt invoke failed for ${Id}: $result" }
    [IO.File]::WriteAllText((Join-Path $output "$prefix-invoke.txt"), ($result | Out-String).ReplaceLineEndings("`r`n"))
}
try {
    $state = Wait-State 'ready' 0
    $script:hwnd = $state.Hwnd
    $actions = if ($Baseline) { @('ReproCarousel','RoundTrip','Preview','OpenFromPreview','VerifyDetails','Stress','Collect','Stress','Collect','Stress','Collect') } else { @('ReproCarousel','RoundTrip','Preview','VerifyDetails','Secondary','Universal','DetailsTiming','CarouselEdges','CoverCancel','HoverExit','HoverFrames','HoverWheel','CoverCycles','Stress','Collect','Stress','Collect','Stress','Collect') }
    if ($OnlyActions) { $actions = $OnlyActions }
    foreach ($action in $actions) {
        Invoke-Button $action
        $state = Wait-State $action $state.Sequence
        if (-not $Baseline -and $action -eq 'Preview') { Invoke-Button 'SongPreview' }
        Write-Output "$action : $($state.Status) ($($state.Checks.Count) checks)"
    }
    [IO.File]::WriteAllText((Join-Path $output 'result.json'), ($state | ConvertTo-Json -Depth 8).ReplaceLineEndings("`r`n"))
    Get-ChildItem -LiteralPath (Split-Path $executable) -Filter 'home-polish-*.png' | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $output $_.Name) -Force
    }
    if ($CaptureDump) {
        & 'C:/Users/90684/.dotnet/tools/dotnet-dump.exe' collect -p $process.Id --type Heap -o (Join-Path $output 'post-gc.dmp')
        if ($LASTEXITCODE -ne 0) { throw 'Heap capture failed' }
    }
    Invoke-Button 'Exit' 
    if (-not $process.WaitForExit(10000)) { throw 'Fixture did not exit' }
    if ($Baseline) { Write-Output "BASELINE: $($state.Failures) observed failures, $($state.Checks.Count) successful checks. Results: $output" }
    else { Write-Output "PASS: $($state.Checks.Count) checks, $script:index lvt button invocations, no foreground activation. Results: $output" }
} finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id }
}
