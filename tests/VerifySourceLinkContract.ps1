[CmdletBinding()]
param(
    [string]$SymbolPackage = (Join-Path $PSScriptRoot '../artifacts/packages/KeelMatrix.KeyRingGuard.0.1.0.snupkg'),
    [string]$Commit = (git -C (Join-Path $PSScriptRoot '..') rev-parse HEAD)
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../build/SourceLink.ps1')
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$data = Read-PackageSourceLink $SymbolPackage
$result = Assert-SourceLinkDocuments $data $root $Commit
Write-Output "SourceLink artifact: PASS ($($result.Tracked) tracked/checksummed sources, $($result.Embedded) embedded/checksummed generated sources)."

$tracked = $data.Documents | Where-Object { -not $_.Embedded } | Select-Object -First 1
$generated = $data.Documents | Where-Object Embedded | Select-Object -First 1
$negativeCases = @(
    @{ Name = 'root-path'; Target = $tracked; Property = 'Name'; Value = '/_/' + [IO.Path]::GetFileName($tracked.Name) }
    @{ Name = 'private-path'; Target = $tracked; Property = 'Name'; Value = 'C:/private/source.cs' }
    @{ Name = 'private-generated-path'; Target = $generated; Property = 'Name'; Value = 'C:/private/generated.cs' }
    @{ Name = 'tracked-checksum'; Target = $tracked; Property = 'Checksum'; Value = '00' }
    @{ Name = 'unknown-checksum'; Target = $tracked; Property = 'Algorithm'; Value = [guid]::Empty }
    @{ Name = 'embedded-checksum'; Target = $generated; Property = 'Checksum'; Value = '00' }
    @{ Name = 'generated-not-embedded'; Target = $generated; Property = 'Embedded'; Value = $false }
    @{ Name = 'tracked-embedded'; Target = $tracked; Property = 'Embedded'; Value = $true }
)
foreach ($case in $negativeCases) {
    $old = $case.Target.($case.Property)
    try {
        $case.Target.($case.Property) = $case.Value
        $rejected = $false
        try { $null = Assert-SourceLinkDocuments $data $root $Commit }
        catch { $rejected = $_.Exception.Message.StartsWith('SourceLink contract:') }
        if (-not $rejected) { throw "SourceLink negative case $($case.Name) was not rejected." }
    } finally { $case.Target.($case.Property) = $old }
}
$oldMapping = $data.SourceLink
try {
    $data.SourceLink = @{ documents = @{ '/_/*' = 'https://raw.githubusercontent.com/KeelMatrix/KeyRingGuard/0000000000000000000000000000000000000000/*' } }
    $rejected = $false
    try { $null = Assert-SourceLinkDocuments $data $root $Commit }
    catch { $rejected = $_.Exception.Message.StartsWith('SourceLink contract:') }
    if (-not $rejected) { throw 'SourceLink wrong-commit case was not rejected.' }
} finally { $data.SourceLink = $oldMapping }
$oldDocuments = $data.Documents
try {
    foreach ($documents in @(
            @{ Items = @($oldDocuments | Where-Object { $_ -ne $tracked }) }
            @{ Items = @($oldDocuments) + @($tracked) }
        )) {
        $data.Documents = $documents.Items
        $rejected = $false
        try { $null = Assert-SourceLinkDocuments $data $root $Commit }
        catch { $rejected = $_.Exception.Message.StartsWith('SourceLink contract:') }
        if (-not $rejected) { throw 'SourceLink incomplete/duplicate source set was not rejected.' }
    }
} finally { $data.Documents = $oldDocuments }
Write-Output 'SourceLink regressions: PASS (mapped path, private path, tracked/embedded checksums, source classification and commit mismatch).'
