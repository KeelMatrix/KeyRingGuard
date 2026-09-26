[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
)

$ErrorActionPreference = 'Stop'
$contract = Join-Path $RepositoryRoot 'scripts' 'Test-ChangelogContract.ps1'
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('keyringguard-changelog-contract-' + [Guid]::NewGuid().ToString('N'))

function Invoke-Contract([string]$Root, [string]$Tag) {
    $output = @(& pwsh -NoProfile -File $contract -Tag $Tag -RepositoryRoot $Root -ChangelogPath (Join-Path $Root 'CHANGELOG.md') -PackageVersionPath (Join-Path $Root 'Directory.Build.props') -CentralPackageVersionPath (Join-Path $Root 'Directory.Packages.props') 2>&1)
    [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join [Environment]::NewLine).Trim() }
}

function Write-Fixture([string]$Root, [string]$Heading, [string]$Version = '0.1.0', [string]$CentralVersion = '0.1.0') {
    New-Item -ItemType Directory -Force -Path $Root | Out-Null
    Set-Content -LiteralPath (Join-Path $Root 'Directory.Build.props') -Value "<Project><PropertyGroup><Version>$Version</Version></PropertyGroup></Project>" -NoNewline
    $centralProps = '<Project><ItemGroup><PackageVersion Include="KeelMatrix.KeyRingGuard" Version="' + $CentralVersion + '" /></ItemGroup></Project>'
    Set-Content -LiteralPath (Join-Path $Root 'Directory.Packages.props') -Value $centralProps -NoNewline
    Set-Content -LiteralPath (Join-Path $Root 'CHANGELOG.md') -Value "# Changelog`n`n## [Unreleased]`n`n$Heading`n`n- Synthetic release notes.`n" -NoNewline
}

New-Item -ItemType Directory -Force -Path $scratch | Out-Null
try {
    $planned = Join-Path $scratch 'planned'
    Write-Fixture $planned '## [0.1.0] - Planned (not yet published)'
    $result = Invoke-Contract $planned 'v0.1.0'
    if ($result.ExitCode -eq 0) { throw 'A planned release heading must fail.' }

    $finalized = Join-Path $scratch 'finalized'
    Write-Fixture $finalized "## [0.1.0] - $((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd'))`n`n### Added`n`n- Provides the documented continuity verification path."
    $result = Invoke-Contract $finalized 'v0.1.0'
    if ($result.ExitCode -ne 0) { throw "A finalized release heading should pass: $($result.Output)" }

    $nonAdded = Join-Path $scratch 'non-added'
    Write-Fixture $nonAdded "## [0.1.0] - $((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd'))`n`n### Fixed`n`n- Corrected an unreleased implementation detail."
    $result = Invoke-Contract $nonAdded 'v0.1.0'
    if ($result.ExitCode -eq 0) { throw 'A first release with a non-Added section must fail.' }

    $remediation = Join-Path $scratch 'remediation'
    Write-Fixture $remediation "## [0.1.0] - $((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd'))`n`n### Added`n`n- The package now verifies continuity."
    $result = Invoke-Contract $remediation 'v0.1.0'
    if ($result.ExitCode -eq 0) { throw 'A first-release remediation marker must fail.' }

    $mismatch = Join-Path $scratch 'mismatch'
    Write-Fixture $mismatch "## [0.1.0] - 2026-09-24`n`n### Added`n`n- Provides the documented continuity verification path." -CentralVersion '0.1.1'
    $result = Invoke-Contract $mismatch 'v0.1.0'
    if ($result.ExitCode -eq 0) { throw 'A central package version mismatch must fail.' }

Write-Output 'Changelog contract: PASS (planned, finalized, and mismatch cases are fail-closed).'
exit 0
}
finally {
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
