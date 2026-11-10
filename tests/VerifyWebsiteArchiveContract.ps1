[CmdletBinding()]
param(
    [string]$Version = $env:RELEASE_VERSION,
    [string]$PackagePath = ''
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = '0.1.0' }
. (Join-Path $PSScriptRoot '../build/Invoke-NestedPwsh.ps1')
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $PackagePath) { $PackagePath = Join-Path $root "artifacts/packages/KeelMatrix.KeyRingGuard.$Version.nupkg" }
$symbolPath = [IO.Path]::ChangeExtension($PackagePath, '.snupkg')
$validator = Join-Path $root 'scripts/Test-WebsiteMetadata.ps1'
$inspector = Join-Path $root 'scripts/Inspect-Package.ps1'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$scratch = Join-Path $tempRoot ('keyringguard-archive-metadata-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $scratch
function Read-Nuspec([string]$Path) {
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $reader = [IO.StreamReader]::new($zip.GetEntry('KeelMatrix.KeyRingGuard.nuspec').Open())
        try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    finally { $zip.Dispose() }
}
function Write-NuspecArchive([string]$Path, [string]$Text, [bool]$DuplicateRoot = $false) {
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path }
    $zip = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $names = @('KeelMatrix.KeyRingGuard.nuspec')
        if ($DuplicateRoot) { $names += 'Second.NUSPEC' }
        foreach ($name in $names) {
            $writer = [IO.StreamWriter]::new($zip.CreateEntry($name).Open())
            try { $writer.Write($Text) } finally { $writer.Dispose() }
        }
    }
    finally { $zip.Dispose() }
}
$mainText = Read-Nuspec $PackagePath
$symbolText = Read-Nuspec $symbolPath
$mainFixture = Join-Path $scratch "KeelMatrix.KeyRingGuard.$Version.nupkg"
$symbolFixture = [IO.Path]::ChangeExtension($mainFixture, '.snupkg')
$mutations = @(
    @{ Name = 'missing sentinels'; Change = { param($s) $s.Replace('keelmatrix-public-product', '').Replace('keelmatrix-primary', '') } },
    @{ Name = 'missing visibility'; Change = { param($s) $s.Replace('keelmatrix-public-product', '') } },
    @{ Name = 'missing role'; Change = { param($s) $s.Replace('keelmatrix-primary', '') } },
    @{ Name = 'duplicate visibility'; Change = { param($s) $s.Replace('</tags>', ' keelmatrix-public-product</tags>') } },
    @{ Name = 'duplicate role'; Change = { param($s) $s.Replace('</tags>', ' keelmatrix-primary</tags>') } },
    @{ Name = 'conflicting visibility'; Change = { param($s) $s.Replace('</tags>', ' keelmatrix-internal-package</tags>') } },
    @{ Name = 'conflicting role'; Change = { param($s) $s.Replace('</tags>', ' keelmatrix-component</tags>') } },
    @{ Name = 'visibility case'; Change = { param($s) $s.Replace('keelmatrix-public-product', 'KeelMatrix-Public-Product') } },
    @{ Name = 'role case'; Change = { param($s) $s.Replace('keelmatrix-primary', 'KEELMATRIX-PRIMARY') } },
    @{ Name = 'lost marketing tag'; Change = { param($s) $s.Replace('aspnetcore ', '') } },
    @{ Name = 'changed marketing tag'; Change = { param($s) $s.Replace('aspnetcore ', 'changed ') } },
    @{ Name = 'extra marketing tag'; Change = { param($s) $s.Replace('</tags>', ' extra</tags>') } },
    @{ Name = 'wrong ID'; Change = { param($s) $s.Replace('<id>KeelMatrix.KeyRingGuard</id>', '<id>KeelMatrix.Other</id>') } },
    @{ Name = 'ID case'; Change = { param($s) $s.Replace('<id>KeelMatrix.KeyRingGuard</id>', '<id>keelmatrix.keyringguard</id>') } },
    @{ Name = 'project URL'; Change = { param($s) $s.Replace('<projectUrl>https://github.com/KeelMatrix/KeyRingGuard#readme</projectUrl>', '<projectUrl>https://github.com/Other/Elsewhere</projectUrl>') } },
    @{ Name = 'repository URL'; Change = { param($s) $s.Replace('url="https://github.com/KeelMatrix/KeyRingGuard"', 'url="https://github.com/Other/Elsewhere"') } },
    @{ Name = 'repository type'; Change = { param($s) $s.Replace('type="git"', 'type="Git"') } },
    @{ Name = 'missing repository URL'; Change = { param($s) $s.Replace('url="https://github.com/KeelMatrix/KeyRingGuard"', '') } },
    @{ Name = 'missing repository type'; Change = { param($s) $s.Replace('type="git"', '') } },
    @{ Name = 'duplicate metadata'; Change = { param($s) $s.Replace('</package>', '<metadata><id>ambiguous</id></metadata></package>') } },
    @{ Name = 'missing metadata'; Change = { param($s) [regex]::Replace($s, '(?s)<metadata>.*</metadata>', '') } },
    @{ Name = 'duplicate ID'; Change = { param($s) $s.Replace('</id>', '</id><id>KeelMatrix.KeyRingGuard</id>') } },
    @{ Name = 'missing ID'; Change = { param($s) $s.Replace('<id>KeelMatrix.KeyRingGuard</id>', '') } },
    @{ Name = 'nested ID'; Change = { param($s) $s.Replace('<id>KeelMatrix.KeyRingGuard</id>', '<id><value>KeelMatrix.KeyRingGuard</value></id>') } },
    @{ Name = 'duplicate tags'; Change = { param($s) $s.Replace('</tags>', '</tags><tags>ambiguous</tags>') } },
    @{ Name = 'missing tags'; Change = { param($s) [regex]::Replace($s, '<tags>[^<]*</tags>', '') } },
    @{ Name = 'nested tags'; Change = { param($s) $s.Replace('<tags>', '<tags><value>').Replace('</tags>', '</value></tags>') } },
    @{ Name = 'duplicate project URL'; Change = { param($s) $s.Replace('</projectUrl>', '</projectUrl><projectUrl>ambiguous</projectUrl>') } },
    @{ Name = 'missing project URL'; Change = { param($s) [regex]::Replace($s, '<projectUrl>[^<]*</projectUrl>', '') } },
    @{ Name = 'nested project URL'; Change = { param($s) $s.Replace('<projectUrl>', '<projectUrl><value>').Replace('</projectUrl>', '</value></projectUrl>') } },
    @{ Name = 'duplicate repository'; Change = { param($s) $s.Replace('</metadata>', '<repository type="git" url="https://github.com/KeelMatrix/KeyRingGuard" /></metadata>') } },
    @{ Name = 'missing repository'; Change = { param($s) [regex]::Replace($s, '<repository [^>]*/>', '') } },
    @{ Name = 'nested repository'; Change = { param($s) $s.Replace('</metadata>', '<repository><value>ambiguous</value></repository></metadata>') } },
    @{ Name = 'wrong package root'; Change = { param($s) $s.Replace('<package ', '<wrong ').Replace('</package>', '</wrong>') } },
    @{ Name = 'malformed XML'; Change = { param($s) $s.Replace('</metadata>', '') } },
    @{ Name = 'DTD'; Change = { param($s) $s.Replace('<package ', '<!DOCTYPE package [<!ENTITY x "unused">]><package ') } },
    @{ Name = 'duplicate root nuspec'; Change = { param($s) $s }; DuplicateRoot = $true }
)
$count = 0
try {
    foreach ($side in @('symbols', 'main')) {
        Write-NuspecArchive $mainFixture $mainText
        Write-NuspecArchive $symbolFixture $symbolText
        $null = & $validator -PackagePath $mainFixture -SymbolPackagePath $symbolFixture
        foreach ($mutation in $mutations) {
            $original = if ($side -eq 'main') { $mainText } else { $symbolText }
            $changed = & $mutation.Change $original
            if ($changed -ceq $original -and -not $mutation.DuplicateRoot) { throw "Archive mutation did not apply: $($mutation.Name)." }
            $path = if ($side -eq 'main') { $mainFixture } else { $symbolFixture }
            Write-NuspecArchive $path $changed ([bool]$mutation.DuplicateRoot)
            $rejected = $false
            try { $null = & $inspector -Version $Version -PackagePath $mainFixture -AllowMissingIcon }
            catch {
                if ($_.Exception.Message -notmatch '^(Website|Built package)') { throw "Wrong rejection for $side/$($mutation.Name): $($_.Exception.Message)" }
                $rejected = $true
            }
            if (-not $rejected) { throw "Actual inspector accepted $side/$($mutation.Name)." }
            $count++
        }
        # Native workflow invocation and the local checked-step boundary must both stop later work.
        Write-NuspecArchive $mainFixture $mainText
        Write-NuspecArchive $symbolFixture $symbolText
        $text = if ($side -eq 'main') { $mainText } else { $symbolText }
        $path = if ($side -eq 'main') { $mainFixture } else { $symbolFixture }
        Write-NuspecArchive $path ($text.Replace('keelmatrix-primary', 'KEELMATRIX-PRIMARY'))
        $arguments = @('-Version', $Version, '-PackagePath', $mainFixture, '-AllowMissingIcon')
        $output = @(Invoke-NestedPwsh -NoProfile -File $inspector @arguments 2>&1)
        if ($LASTEXITCODE -eq 0 -or ($output -join ' ') -notmatch 'Website classification tags') { throw "Native inspector bypassed $side metadata." }
        $argumentsPath = Join-Path $scratch 'arguments.json'
        [IO.File]::WriteAllText($argumentsPath, (ConvertTo-Json -InputObject $arguments))
        $marker = Join-Path $scratch "following-$side"
        $null = @(Invoke-NestedPwsh -NoProfile -File (Join-Path $PSScriptRoot 'Invoke-GateFailureProbe.ps1') -CommandFile $inspector -ArgumentsPath $argumentsPath -MarkerPath $marker 2>&1)
        if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $marker)) { throw "Invalid $side metadata advanced to a following operation." }
    }
    # Real unmodified archives retain payload, README, PDB and SourceLink acceptance.
    $arguments = @('-Version', $Version, '-PackagePath', $PackagePath, '-AllowMissingIcon')
    $argumentsPath = Join-Path $scratch 'arguments.json'
    [IO.File]::WriteAllText($argumentsPath, (ConvertTo-Json -InputObject $arguments))
    $marker = Join-Path $scratch 'following-valid'
    $validOutput = @(Invoke-NestedPwsh -NoProfile -File (Join-Path $PSScriptRoot 'Invoke-GateFailureProbe.ps1') -CommandFile $inspector -ArgumentsPath $argumentsPath -MarkerPath $marker 2>&1)
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $marker)) { throw 'Valid paired inspection did not advance to the following operation.' }
    if (($validOutput -join ' ') -notmatch 'SourceLink inspection: PASS') { throw 'Valid paired inspection bypassed SourceLink.' }
    Write-Output "Website archive contract: PASS ($count paired metadata mutations; real inspector/native/checked failure controls and valid payload/SourceLink control)."
}
finally {
    if (-not ([IO.Path]::GetFullPath($scratch)).StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe archive fixture cleanup path.' }
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
