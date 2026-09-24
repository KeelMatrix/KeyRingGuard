[CmdletBinding()]
param(
    [string]$PackagePath = (Join-Path $PSScriptRoot '..' 'artifacts' 'packages' 'KeelMatrix.KeyRingGuard.0.1.0.nupkg')
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ('keyringguard-package-smoke-' + [Guid]::NewGuid().ToString('N'))
$feed = Join-Path $scratch 'feed'
$packages = Join-Path $scratch 'packages'
$nugetConfig = Join-Path $scratch 'NuGet.config'
New-Item -ItemType Directory -Force -Path $feed | Out-Null
New-Item -ItemType Directory -Force -Path $packages | Out-Null
Copy-Item -LiteralPath $package -Destination $feed
$escapedFeed = [Security.SecurityElement]::Escape($feed)
Set-Content -LiteralPath $nugetConfig -Value @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$escapedFeed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
"@ -NoNewline

$previousNuGetPackages = $env:NUGET_PACKAGES
$env:NUGET_PACKAGES = $packages
try {
    $consumer = Join-Path $root 'tests' 'KeelMatrix.KeyRingGuard.PackageConsumer' 'KeelMatrix.KeyRingGuard.PackageConsumer.csproj'
    dotnet restore $consumer --configfile $nugetConfig --no-cache --force --packages $packages
    if ($LASTEXITCODE -ne 0) { throw "Package consumer restore failed with exit code $LASTEXITCODE." }
    dotnet run --project $consumer --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Package consumer smoke failed with exit code $LASTEXITCODE." }
    Write-Output 'Package consumer smoke: PASS'
} finally {
    if ($null -eq $previousNuGetPackages) { Remove-Item Env:NUGET_PACKAGES -ErrorAction SilentlyContinue } else { $env:NUGET_PACKAGES = $previousNuGetPackages }
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
