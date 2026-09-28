[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$targets = Get-Content -Raw -LiteralPath (Join-Path $root 'Directory.Build.targets')
$project = Get-Content -Raw -LiteralPath (Join-Path $root 'src' 'KeelMatrix.KeyRingGuard' 'KeelMatrix.KeyRingGuard.csproj')
$inspector = Get-Content -Raw -LiteralPath (Join-Path $root 'scripts' 'Inspect-Package.ps1')
$ci = Get-Content -Raw -LiteralPath (Join-Path $root '.github' 'workflows' 'ci.yml')
$release = Get-Content -Raw -LiteralPath (Join-Path $root '.github' 'workflows' 'release.yml')

if ($targets -notmatch 'RequirePackageIcon.*!Exists') {
    throw 'Package icon contract failed: the pack guard is not gated by RequirePackageIcon.'
}
$iconItemStart = $project.IndexOf('<None Include="$(MSBuildProjectDirectory)/../../icon.png"', [StringComparison]::Ordinal)
if ($project -notmatch 'PackageIcon Condition="Exists' -or $iconItemStart -lt 0 -or
    $project.Substring($iconItemStart).IndexOf('Condition="Exists', [StringComparison]::Ordinal) -lt 0) {
    throw 'Package icon contract failed: package icon metadata is not conditional on the resolved icon path.'
}
if (-not $inspector.Contains('[switch]$AllowMissingIcon', [StringComparison]::Ordinal)) {
    throw 'Package icon contract failed: package inspection has no explicit readiness allowance.'
}
if (-not $ci.Contains('-p:RequirePackageIcon=false', [StringComparison]::Ordinal) -or
    -not $ci.Contains('Inspect-Package.ps1 -Version 0.1.0 -PackagePath ./artifacts/packages/KeelMatrix.KeyRingGuard.0.1.0.nupkg -AllowMissingIcon', [StringComparison]::Ordinal)) {
    throw 'Package icon contract failed: readiness CI does not use the optional-icon pack and inspection paths.'
}
if (-not $release.Contains('-p:RequirePackageIcon=true', [StringComparison]::Ordinal) -or
    $release.Contains('Inspect-Package.ps1 -AllowMissingIcon', [StringComparison]::Ordinal)) {
    throw 'Package icon contract failed: release validation is not strict about the icon.'
}

Write-Output 'Package icon contract: PASS (optional readiness packaging, strict release packaging, and conditional verification).'
