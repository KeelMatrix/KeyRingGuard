[CmdletBinding()]
param(
    [string]$Version = $env:RELEASE_VERSION,
    [string]$PackageDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..' 'artifacts' 'packages')),
    [string]$ApiKey = $env:NUGET_API_KEY,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Version)) { throw 'The release version is missing from RELEASE_VERSION.' }
if (-not (Test-Path -LiteralPath $PackageDirectory -PathType Container)) {
    throw "Release artifact directory '$PackageDirectory' does not exist."
}

$expected = @(
    "KeelMatrix.KeyRingGuard.$Version.nupkg"
    "KeelMatrix.KeyRingGuard.$Version.snupkg"
)
$actual = @(Get-ChildItem -LiteralPath $PackageDirectory -File | Select-Object -ExpandProperty Name)
if ((Compare-Object ($expected | Sort-Object) ($actual | Sort-Object)) -ne $null) {
    throw "Unexpected release artifact set. Expected: $($expected -join ', '). Actual: $($actual -join ', ')."
}

$packagePaths = @($expected | ForEach-Object { Join-Path $PackageDirectory $_ })
foreach ($packagePath in $packagePaths) {
    if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
        throw "Expected release artifact '$packagePath' does not exist."
    }
}

$pushCommandSuffix = '--source https://api.nuget.org/v3/index.json --api-key <temporary credential>'
if ($DryRun) {
    foreach ($packagePath in $packagePaths) {
        Write-Output "Dry run: dotnet nuget push $packagePath $pushCommandSuffix"
    }
    exit 0
}

if ([string]::IsNullOrWhiteSpace($ApiKey)) { throw 'The temporary NuGet credential is missing from NUGET_API_KEY.' }

foreach ($packagePath in $packagePaths) {
    & dotnet nuget push $packagePath --source https://api.nuget.org/v3/index.json --api-key $ApiKey
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Output "Published release artifacts for version $Version."
