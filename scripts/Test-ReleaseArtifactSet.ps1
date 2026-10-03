[CmdletBinding()]
param(
    [string]$Version = $env:RELEASE_VERSION,
    [string]$PackageDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..' 'artifacts' 'packages'))
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Version)) { throw 'The release version is missing from RELEASE_VERSION.' }
if (-not (Test-Path -LiteralPath $PackageDirectory -PathType Container)) {
    throw "Release artifact directory '$PackageDirectory' does not exist."
}

$expected = @(
    "KeelMatrix.KeyRingGuard.$Version.nupkg"
    "KeelMatrix.KeyRingGuard.$Version.snupkg"
) | Sort-Object
$actual = @(Get-ChildItem -LiteralPath $PackageDirectory -File | Select-Object -ExpandProperty Name | Sort-Object)
if ((Compare-Object $expected $actual) -ne $null) {
    throw "Unexpected release artifact set. Expected: $($expected -join ', '). Actual: $($actual -join ', ')."
}

Write-Output "Release artifact set: PASS ($($actual -join ', '))."
