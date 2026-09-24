[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location $root

function Invoke-GateStep([string]$Name, [scriptblock]$Command) {
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    Write-Output "== $Name =="
    try {
        & $Command
        if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE." }
        $watch.Stop()
        Write-Output ("{0}: PASS ({1} ms)" -f $Name, $watch.ElapsedMilliseconds)
    }
    catch {
        $watch.Stop()
        Write-Output ("{0}: FAIL ({1} ms)" -f $Name, $watch.ElapsedMilliseconds)
        throw
    }
}

Invoke-GateStep 'Restore' { dotnet restore KeelMatrix.KeyRingGuard.sln }
Invoke-GateStep 'Release build and public API analyzers' { dotnet build KeelMatrix.KeyRingGuard.sln --configuration Release --no-restore }
Invoke-GateStep 'Tests' { dotnet test KeelMatrix.KeyRingGuard.sln --configuration Release --no-build }
Invoke-GateStep 'Changelog contract tests' { & (Join-Path $root 'tests' 'VerifyChangelogContract.ps1') }
Invoke-GateStep 'Release workflow contract' { & (Join-Path $root 'tests' 'VerifyReleaseWorkflowContract.ps1') }
Invoke-GateStep 'Release script portability contract' { & (Join-Path $root 'tests' 'VerifyReleaseScriptPortability.ps1') }
Invoke-GateStep 'Vulnerability audit contract tests' { & (Join-Path $root 'tests' 'VerifyVulnerabilityAuditContract.ps1') }
Invoke-GateStep 'Formatting' { dotnet format KeelMatrix.KeyRingGuard.sln --no-restore --verify-no-changes }
Invoke-GateStep 'Icon source contract' { & (Join-Path $PSScriptRoot 'Test-IconContract.ps1') -SourceOnly }
Invoke-GateStep 'Pack' { dotnet pack src/KeelMatrix.KeyRingGuard/KeelMatrix.KeyRingGuard.csproj --configuration Release --no-build --include-symbols --p:SymbolPackageFormat=snupkg --output ./artifacts/packages }
Invoke-GateStep 'Icon contract' { & (Join-Path $PSScriptRoot 'Test-IconContract.ps1') }
Invoke-GateStep 'Package content inspection' { & (Join-Path $PSScriptRoot 'Inspect-Package.ps1') }
Invoke-GateStep 'Dependency vulnerability audit' { & (Join-Path $PSScriptRoot 'Invoke-VulnerabilityAudit.ps1') }
Invoke-GateStep 'Package consumer smoke' { & (Join-Path $PSScriptRoot 'Invoke-PackageConsumerSmoke.ps1') }
Invoke-GateStep 'History and workspace hygiene' { & (Join-Path $PSScriptRoot 'Test-HistoryHygiene.ps1') }

Write-Output 'LOCAL_GATE: PASS'
