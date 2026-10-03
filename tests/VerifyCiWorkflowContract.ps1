[CmdletBinding()]
param(
    [string]$WorkflowPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..' '.github' 'workflows' 'ci.yml'))
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $WorkflowPath -PathType Leaf)) { throw "CI workflow is missing: $WorkflowPath" }
$text = Get-Content -Raw -LiteralPath $WorkflowPath

$requiredFragments = @(
    'ubuntu-latest'
    'windows-latest'
    'macos-latest'
    'dotnet restore KeelMatrix.KeyRingGuard.sln --configfile NuGet.config --no-cache --force'
    'dotnet format KeelMatrix.KeyRingGuard.sln --no-restore --verify-no-changes'
    'dotnet build KeelMatrix.KeyRingGuard.sln --configuration Release --no-restore'
    'dotnet test KeelMatrix.KeyRingGuard.sln --configuration Release --no-build'
    'dotnet pack src/KeelMatrix.KeyRingGuard/KeelMatrix.KeyRingGuard.csproj --configuration Release'
    'scripts/Test-ChangelogContract.ps1'
    'tests/VerifyReleaseWorkflowContract.ps1'
    'tests/VerifyPackageIconContract.ps1'
    'tests/VerifyVulnerabilityAuditContract.ps1'
    'tests/VerifyReleaseScriptPortability.ps1'
    'tests/VerifyHistoryHygieneContract.ps1'
    'scripts/Test-HistoryHygiene.ps1'
    'scripts/Invoke-VulnerabilityAudit.ps1'
    'scripts/Inspect-Package.ps1 -Version 0.1.0 -PackagePath ./artifacts/packages/KeelMatrix.KeyRingGuard.0.1.0.nupkg -AllowMissingIcon'
    'scripts/Invoke-PackageConsumerSmoke.ps1'
    'scripts/GetReleaseVersion.ps1 -Tag "$RELEASE_TAG" -OutputPath "$RUNNER_TEMP/release-version.txt"'
    'scripts/Test-ReleaseArtifactSet.ps1 -Version 0.1.0 -PackageDirectory ./artifacts/packages'
    'scripts/Publish-ReleaseArtifacts.ps1 -Version 0.1.0 -PackageDirectory ./artifacts/packages -DryRun'
)
foreach ($fragment in $requiredFragments) {
    if ($text.IndexOf($fragment, [StringComparison]::Ordinal) -lt 0) {
        throw "CI workflow contract failed: required fragment '$fragment' is missing."
    }
}

if ($text.IndexOf("if: hashFiles('icon.png') != ''", [StringComparison]::Ordinal) -ge 0) {
    throw 'CI workflow contract failed: required package/icon inspection must not be conditionally skipped.'
}
if ($text.IndexOf('-p:RequirePackageIcon=true', [StringComparison]::Ordinal) -ge 0) {
    throw 'CI workflow contract failed: readiness CI must not require the founder-owned icon.'
}
if ($text -notmatch "(?m)^\s*if: matrix\.os == 'ubuntu-latest'\s*$") {
    throw 'CI workflow contract failed: the release PowerShell backstop must run only on Ubuntu.'
}
if ($text -match '(?i)NUGET_API_KEY|dotnet\s+nuget\s+push') {
    throw 'CI workflow contract failed: the Ubuntu release PowerShell backstop must not use a publish credential or execute a real push.'
}

Write-Output 'CI workflow contract: PASS (cross-platform matrix, release-equivalent contracts, Ubuntu release-script dry run without publication, fail-closed icon-aware package inspection, and consumer smoke are present).'
exit 0
