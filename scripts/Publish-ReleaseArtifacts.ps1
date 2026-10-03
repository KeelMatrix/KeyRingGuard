[CmdletBinding()]
param(
    [string]$Version = $env:RELEASE_VERSION,
    [string]$PackageDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..' 'artifacts' 'packages')),
    [string]$ApiKey = $env:NUGET_API_KEY
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Version)) { throw 'The release version is missing from RELEASE_VERSION.' }
if ([string]::IsNullOrWhiteSpace($ApiKey)) { throw 'The temporary NuGet credential is missing from NUGET_API_KEY.' }

foreach ($extension in @('nupkg', 'snupkg')) {
    $packagePath = Join-Path $PackageDirectory "KeelMatrix.KeyRingGuard.$Version.$extension"
    & dotnet nuget push $packagePath --source https://api.nuget.org/v3/index.json --api-key $ApiKey
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Output "Published release artifacts for version $Version."
