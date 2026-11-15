param([switch]$Packaged)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$publish = Join-Path $repo 'artifacts/single-instance-20261115/fixture-publish'
$build = Join-Path $PSScriptRoot 'bin/Release/net11.0-windows10.0.26100.0/win-x64'
Get-ChildItem -LiteralPath $build -Recurse -Filter *.xbf | ForEach-Object {
    $destination = Join-Path $publish $_.FullName.Substring($build.Length + 1)
    New-Item -ItemType Directory -Force (Split-Path $destination) | Out-Null
    Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
}
Get-ChildItem -LiteralPath $build -Filter *.pri | Copy-Item -Destination $publish -Force
$mode = if ($Packaged) { 'packaged' } else { 'unpackaged' }
$work = (New-Item -ItemType Directory -Force (Join-Path $repo ('artifacts/single-instance-20261115/' + $mode + '-' + (Get-Date -Format 'HHmmss')))).FullName
$song = Join-Path $work 'initial song.wav'
[IO.File]::WriteAllBytes($song, [byte[]]@())
$key = 'OriginalSound.SingleInstanceRegression.' + [guid]::NewGuid().ToString('N')
$arguments = '"{0}" "{1}" "{2}"' -f $key, $work, $song
$executable = Join-Path $publish 'SingleInstanceRegression.exe'
if ($Packaged) {
    # 为测试 EXE 授予现有 AppX 包身份，不启动播放器，也不修改包注册。
    Invoke-CommandInDesktopPackage -PackageFamilyName 'SennpaiStudio.528762A6196EF_z79ft30j24epr' -AppId App -Command $executable -Args ($arguments + ' --packaged') -PreventBreakaway
}
else {
    $process = Start-Process -FilePath $executable -ArgumentList $arguments -WindowStyle Hidden -PassThru
    try { $process.PriorityClass = 'BelowNormal' } catch { }
}
$timer = [Diagnostics.Stopwatch]::StartNew()
while (-not (Test-Path -LiteralPath (Join-Path $work 'exit.txt'))) {
    if ($timer.Elapsed.TotalSeconds -gt 40) { throw ('Fixture timed out: ' + $work) }
    Start-Sleep -Milliseconds 100
}
Get-Content -LiteralPath (Join-Path $work 'result.txt')
Get-ChildItem -LiteralPath $work -Filter 'error*.txt' | ForEach-Object { Get-Content -LiteralPath $_.FullName }
if ([IO.File]::ReadAllText((Join-Path $work 'exit.txt')) -ne '0') { throw ('Fixture failed: ' + $work) }
Write-Output ('Results: ' + $work)
