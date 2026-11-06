[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $root

. (Join-Path $root 'build' 'Invoke-GateStep.ps1')

Invoke-GateStep 'SDK selection' { & (Join-Path $PSScriptRoot 'Test-SdkSelection.ps1') }
Invoke-GateStep 'Hidden child PowerShell guard self-test' { & (Join-Path $root 'build' 'Test-NestedPwshLaunch.ps1') -SelfTest }
Invoke-GateStep 'Hidden child PowerShell launch scan' { & (Join-Path $root 'build' 'Test-NestedPwshLaunch.ps1') }
Invoke-GateStep 'Restore' { dotnet restore KeelMatrix.KeyRingGuard.sln }
Invoke-GateStep 'Release build and public API analyzers' { dotnet build KeelMatrix.KeyRingGuard.sln --configuration Release --no-restore }
Invoke-GateStep 'Tests' { dotnet test KeelMatrix.KeyRingGuard.sln --configuration Release --no-build }
Invoke-GateStep 'Changelog contract tests' { & (Join-Path $root 'tests' 'VerifyChangelogContract.ps1') }
Invoke-GateStep 'Readiness changelog' { & (Join-Path $PSScriptRoot 'Test-ChangelogContract.ps1') -Tag v0.1.0 -Readiness }
Invoke-GateStep 'Isolated SDK provisioning' { & (Join-Path $root 'tests' 'VerifySdkIsolation.ps1') }
Invoke-GateStep 'Loopback publication contract' { & (Join-Path $root 'tests' 'VerifyPublicationContract.ps1') }
Invoke-GateStep 'IDE ignore contract' { & (Join-Path $root 'tests' 'VerifyIdeIgnoreContract.ps1') }
Invoke-GateStep 'Gate orchestration' { & (Join-Path $root 'tests' 'VerifyGateOrchestration.ps1') }
Invoke-GateStep 'Release workflow contract' { & (Join-Path $root 'tests' 'VerifyReleaseWorkflowContract.ps1') }
Invoke-GateStep 'Package icon contract' { & (Join-Path $root 'tests' 'VerifyPackageIconContract.ps1') }
Invoke-GateStep 'CI workflow contract' { & (Join-Path $root 'tests' 'VerifyCiWorkflowContract.ps1') }
Invoke-GateStep 'Vulnerability audit contract tests' { & (Join-Path $root 'tests' 'VerifyVulnerabilityAuditContract.ps1') }
Invoke-GateStep 'Formatting' { dotnet format KeelMatrix.KeyRingGuard.sln --no-restore --verify-no-changes }
Invoke-GateStep 'Release script portability contract' { & (Join-Path $root 'tests' 'VerifyReleaseScriptPortability.ps1') }
Invoke-GateStep 'Dependency vulnerability audit' { & (Join-Path $PSScriptRoot 'Invoke-VulnerabilityAudit.ps1') }
Invoke-GateStep 'History hygiene contract tests' { & (Join-Path $root 'tests' 'VerifyHistoryHygieneContract.ps1') }
Invoke-GateStep 'History and workspace hygiene' { & (Join-Path $PSScriptRoot 'Test-HistoryHygiene.ps1') }
Invoke-GateStep 'Pack without requiring the founder-owned icon' { dotnet pack src/KeelMatrix.KeyRingGuard/KeelMatrix.KeyRingGuard.csproj --configuration Release --no-build --include-symbols --p:SymbolPackageFormat=snupkg -p:RequirePackageIcon=false --output ./artifacts/packages }
Invoke-GateStep 'Package content inspection' { & (Join-Path $PSScriptRoot 'Inspect-Package.ps1') -AllowMissingIcon }
Invoke-GateStep 'SourceLink contract tests' { & (Join-Path $root 'tests' 'VerifySourceLinkContract.ps1') }
Invoke-GateStep 'Package consumer smoke' { & (Join-Path $PSScriptRoot 'Invoke-PackageConsumerSmoke.ps1') }

Write-Output 'LOCAL_GATE: PASS'
