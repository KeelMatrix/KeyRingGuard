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

function Write-Fixture([string]$Root, [string]$Heading, [string]$Version = '1.2.3', [string]$CentralVersion = '1.2.3') {
    New-Item -ItemType Directory -Force -Path $Root | Out-Null
    Set-Content -LiteralPath (Join-Path $Root 'Directory.Build.props') -Value "<Project><PropertyGroup><Version>$Version</Version></PropertyGroup></Project>" -NoNewline
    $centralProps = '<Project><ItemGroup><PackageVersion Include="KeelMatrix.KeyRingGuard" Version="' + $CentralVersion + '" /></ItemGroup></Project>'
    Set-Content -LiteralPath (Join-Path $Root 'Directory.Packages.props') -Value $centralProps -NoNewline
    Set-Content -LiteralPath (Join-Path $Root 'CHANGELOG.md') -Value "# Changelog`n`n## [Unreleased]`n`n$Heading`n`n- Synthetic release notes.`n" -NoNewline
}

New-Item -ItemType Directory -Force -Path $scratch | Out-Null
try {
    $planned = Join-Path $scratch 'planned'
    Write-Fixture $planned '## [1.2.3] - Planned (not yet published)'
    $result = Invoke-Contract $planned 'v1.2.3'
    if ($result.ExitCode -eq 0) { throw 'A planned release heading must fail.' }

    $finalized = Join-Path $scratch 'finalized'
    Write-Fixture $finalized "## [1.2.3] - $((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd'))"
    $result = Invoke-Contract $finalized 'v1.2.3'
    if ($result.ExitCode -ne 0) { throw "A finalized release heading should pass: $($result.Output)" }

    $mismatch = Join-Path $scratch 'mismatch'
    Write-Fixture $mismatch '## [1.2.3] - 2026-09-24' -CentralVersion '1.2.4'
    $result = Invoke-Contract $mismatch 'v1.2.3'
    if ($result.ExitCode -eq 0) { throw 'A central package version mismatch must fail.' }

Write-Output 'Changelog contract: PASS (planned, finalized, and mismatch cases are fail-closed).'
exit 0
}
finally {
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
