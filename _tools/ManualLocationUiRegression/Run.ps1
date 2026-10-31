param([switch]$SkipBuild, [switch]$Baseline, [string[]]$OnlyActions)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_CLI_USE_MSBUILD_SERVER = '0'
if (-not $SkipBuild) {
    dotnet build "$PSScriptRoot/ManualLocationUiRegression.csproj" -p:Platform=x64 --no-restore -v:q -clp:ErrorsOnly
    if ($LASTEXITCODE -ne 0) { throw 'Manual location fixture build failed' }
}
$executable = Join-Path $PSScriptRoot 'bin/x64/Debug/net11.0-windows10.0.26100.0/win-x64/ManualLocationUiRegression.exe'
$output = Join-Path (Split-Path $executable) 'artifacts'
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
    $tree = & 'G:/Tool/lvt/lvt.exe' dump --hwnd $script:hwnd --uia --depth 14 --uia-timeout 5000
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
    $actions = if ($OnlyActions) { $OnlyActions } else { @('PrepareSongs','VerifySongs','PrepareFavorites','VerifyFavorites','Lifecycle','VerifyReentry','Navigation','Positions','Edges','ImportSelection','CrossDetails') }
    foreach ($action in $actions) {
        Invoke-Button $action
        $state = Wait-State $action $state.Sequence
        if ($state.Status -eq 'PASS' -and ($action -eq 'PrepareSongs' -or $action -eq 'PrepareFavorites' -or $action -eq 'Lifecycle')) { Invoke-Button 'LocateCurrentTrackButton' }
        Write-Output "$action : $($state.Status) ($($state.Checks.Count) checks)"
    }
    [IO.File]::WriteAllText((Join-Path $output 'result.json'), ($state | ConvertTo-Json -Depth 8).ReplaceLineEndings("`r`n"))
    Invoke-Button 'Exit'
    if (-not $process.WaitForExit(10000)) { throw 'Fixture did not exit' }
    Write-Output "Completed: $($state.Checks.Count) checks, status=$($state.Status), $script:index lvt button invocations, no foreground activation. Results: $output"
} finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id }
}
