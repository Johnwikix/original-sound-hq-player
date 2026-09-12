param([string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -property installationPath
$msbuild = Join-Path $vs 'MSBuild\Current\Bin\MSBuild.exe'
& $msbuild (Join-Path $PSScriptRoot 'RotatingMeshGpuProbe.csproj') /restore /t:Build "/p:Configuration=$Configuration" /p:Platform=x64 /p:NuGetAudit=false /v:minimal /nologo
if ($LASTEXITCODE) { throw 'GPU probe build failed' }
$output = Join-Path $PSScriptRoot "bin\x64\$Configuration\net11.0-windows10.0.26100.0\win-x64"
$stage = Join-Path $PSScriptRoot 'bin\MsixProbe'
New-Item -ItemType Directory -Path $stage,(Join-Path $stage 'Assets') -Force | Out-Null
Copy-Item (Join-Path $output '*') $stage -Recurse -Force
Copy-Item (Join-Path $repo 'Assets\Square150x150Logo.scale-200.png') (Join-Path $stage 'Assets\Logo.png') -Force
Copy-Item (Join-Path $repo 'Assets\Square44x44Logo.scale-200.png') (Join-Path $stage 'Assets\SmallLogo.png') -Force
$manifest = Join-Path $stage 'AppxManifest.xml'
@'
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10" xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10" xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities" IgnorableNamespaces="uap rescap">
  <Identity Name="SennpaiStudio.RotatingMeshGpuProbe" Publisher="CN=ShaderProbe" Version="1.0.0.0" ProcessorArchitecture="x64" />
  <Properties><DisplayName>Rotating mesh GPU probe</DisplayName><PublisherDisplayName>Local validation</PublisherDisplayName><Logo>Assets\Logo.png</Logo></Properties>
  <Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.19041.0" MaxVersionTested="10.0.26100.0" /></Dependencies>
  <Resources><Resource Language="en-US" /></Resources>
  <Applications><Application Id="App" Executable="RotatingMeshGpuProbe.exe" EntryPoint="Windows.FullTrustApplication"><uap:VisualElements DisplayName="Rotating mesh GPU probe" Description="Shader regression" BackgroundColor="transparent" Square150x150Logo="Assets\Logo.png" Square44x44Logo="Assets\SmallLogo.png" AppListEntry="none" /></Application></Applications>
  <Capabilities><rescap:Capability Name="runFullTrust" /></Capabilities>
</Package>
'@ | Set-Content $manifest -Encoding utf8
$result = Join-Path $stage 'shader-gpu-results.txt'
if (Test-Path $result) { Remove-Item -LiteralPath $result }
Add-AppxPackage -Register $manifest
$package = Get-AppxPackage -Name SennpaiStudio.RotatingMeshGpuProbe
try {
    Start-Process explorer.exe -ArgumentList "shell:AppsFolder\$($package.PackageFamilyName)!App" -WindowStyle Hidden
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    do {
        Start-Sleep -Milliseconds 500
        $text = if (Test-Path $result) { Get-Content $result -Raw } else { '' }
        if ($text -match '(?m)^(PASS|FAIL):') { break }
    } while ([DateTime]::UtcNow -lt $deadline)
    $text
    if ($text -notmatch '(?m)^PASS:') { throw "GPU regression did not pass; see $result" }
} finally {
    # Only unregister this probe package; leave application packages alone.
    Remove-AppxPackage -Package $package.PackageFullName
}
