[CmdletBinding()]
param(
    [string]$PackagePath = (Join-Path $PSScriptRoot '..\artifacts\packages\KeelMatrix.KeyRingGuard.0.1.0.nupkg')
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ('keyringguard-package-smoke-' + [Guid]::NewGuid().ToString('N'))
$feed = Join-Path $scratch 'feed'
New-Item -ItemType Directory -Force -Path $feed | Out-Null
Copy-Item -LiteralPath $package -Destination $feed

try {
    $consumer = Join-Path $root 'tests\KeelMatrix.KeyRingGuard.PackageConsumer\KeelMatrix.KeyRingGuard.PackageConsumer.csproj'
    dotnet restore $consumer --source $feed --source 'https://api.nuget.org/v3/index.json'
    if ($LASTEXITCODE -ne 0) { throw "Package consumer restore failed with exit code $LASTEXITCODE." }
    dotnet run --project $consumer --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Package consumer smoke failed with exit code $LASTEXITCODE." }
    Write-Output 'Package consumer smoke: PASS'
} finally {
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
