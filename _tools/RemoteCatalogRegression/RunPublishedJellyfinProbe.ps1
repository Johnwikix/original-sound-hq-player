param(
    [Parameter(Mandatory)][string] $AppDirectory,
    [switch] $Live
)

$ErrorActionPreference = 'Stop'
$publishRoot = (Resolve-Path -LiteralPath $AppDirectory).Path
$scratch = [IO.Directory]::CreateTempSubdirectory('jellyfin-publish-probe-').FullName
try {
    $copy = Join-Path $scratch 'App'
    Copy-Item -LiteralPath $publishRoot -Destination $copy -Recurse
    # MSIX ZIP extraction can leave the package's percent-encoded app name on disk.
    foreach ($fileName in @('OriginalSound HIFI Player.dll', 'OriginalSound HIFI Player.runtimeconfig.json', 'OriginalSound HIFI Player.deps.json')) {
        $encodedCopy = Join-Path $copy $fileName.Replace(' ', '%20')
        if (Test-Path -LiteralPath $encodedCopy) { Rename-Item -LiteralPath $encodedCopy -NewName $fileName }
    }
    $runtimeConfig = Join-Path $copy 'OriginalSound HIFI Player.runtimeconfig.json'
    $deps = Join-Path $copy 'OriginalSound HIFI Player.deps.json'
    $probe = Join-Path $copy 'PublishedJellyfinProbe.dll'
    $dotnet = (Get-Command dotnet -CommandType Application).Source
    $sdk = (& $dotnet --version | Select-Object -Last 1).Trim()
    $compiler = Join-Path (Split-Path $dotnet) "sdk/$sdk/Roslyn/bincore/csc.dll"
    $compilerArgs = @('/nologo', '/noconfig', '/nostdlib+', '/nullable:enable', '/target:exe', "/out:$probe")
    # Reference the package's assemblies directly. SDK framework facades are absent from trimmed packages.
    foreach ($binary in Get-ChildItem -LiteralPath $copy -Filter 'System*.dll' -File) {
        try { [void][Reflection.AssemblyName]::GetAssemblyName($binary.FullName) }
        catch [BadImageFormatException] { continue }
        $compilerArgs += "/reference:$($binary.FullName)"
    }
    $compilerArgs += Join-Path $PSScriptRoot 'PublishedJellyfinProbe.cs'
    & $dotnet $compiler @compilerArgs
    if ($LASTEXITCODE -ne 0) { throw 'Published probe compilation failed.' }
    $probeArgs = @('exec', '--runtimeconfig', $runtimeConfig, '--depsfile', $deps, $probe)
    if ($Live) { $probeArgs += '--live' }
    & $dotnet @probeArgs
    $probeExit = $LASTEXITCODE
    $report = Join-Path $copy 'PublishedProbe.log'
    if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report }
    if ($probeExit -ne 0) { throw "Published Jellyfin sign-in probe failed ($probeExit)." }
}
finally {
    if ((Resolve-Path -LiteralPath $scratch).Path -eq $scratch) { Remove-Item -LiteralPath $scratch -Recurse }
}
