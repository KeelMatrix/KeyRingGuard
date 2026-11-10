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
# The unchanged main archive is hard-linked, so fixtures must share its volume.
$fixtureRoot = [IO.Path]::GetDirectoryName((Resolve-Path -LiteralPath $PackagePath).Path)
$scratch = Join-Path $fixtureRoot ('keyringguard-archive-metadata-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $scratch
function Read-Nuspec([string]$Path) {
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $reader = [IO.StreamReader]::new($zip.GetEntry('KeelMatrix.KeyRingGuard.nuspec').Open())
        try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    finally { $zip.Dispose() }
}
function Write-NuspecArchive([string]$Path, [string]$Text, [bool]$DuplicateRoot = $false, [string]$SymbolPayload = '') {
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path }
    $zip = [IO.Compression.ZipFile]::Open($Path, [IO.Compression.ZipArchiveMode]::Create)
    try {
        if ($SymbolPayload) {
            $original = [IO.Compression.ZipFile]::OpenRead($SymbolPayload)
            try {
                foreach ($entry in $original.Entries) {
                    if ([string]::Equals($entry.FullName, 'KeelMatrix.KeyRingGuard.nuspec', [StringComparison]::Ordinal)) { continue }
                    if ($entry.FullName.EndsWith('.png', [StringComparison]::OrdinalIgnoreCase)) { throw 'Symbol fixtures must not contain images.' }
                    $inputStream = $entry.Open()
                    $outputStream = $zip.CreateEntry($entry.FullName).Open()
                    try { $inputStream.CopyTo($outputStream) } finally { $outputStream.Dispose(); $inputStream.Dispose() }
                }
            }
            finally { $original.Dispose() }
        }
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
foreach ($codePoint in @(0x200B, 0x00AD, 0x2060)) {
    $character = [string][char]$codePoint
    $label = 'U+{0:X4}' -f $codePoint
    foreach ($pair in @(
        @('<id>KeelMatrix.KeyRingGuard</id>', "<id>KeelMatrix.KeyRing${character}Guard</id>"),
        @('<projectUrl>https://github.com/KeelMatrix/KeyRingGuard#readme</projectUrl>', "<projectUrl>https://github.com/KeelMatrix/KeyRing${character}Guard#readme</projectUrl>"),
        @('url="https://github.com/KeelMatrix/KeyRingGuard"', ('url="https://github.com/KeelMatrix/KeyRing' + $character + 'Guard"')),
        @('type="git"', ('type="g' + $character + 'it"')),
        @('aspnetcore ', "aspnet${character}core "),
        @('keelmatrix-public-product', ("keelmatrix-public-product$character")),
        @('keelmatrix-primary', ("keelmatrix-primary$character")),
        @('<package ', ("<pack${character}age "))
    )) {
        $from = $pair[0]; $to = $pair[1]
        $change = { param($s) $s.Replace($from, $to) }.GetNewClosure()
        if ($from.StartsWith('<package ', [StringComparison]::Ordinal)) {
            $change = { param($s) $s.Replace($from, $to).Replace('</package>', "</pack${character}age>") }.GetNewClosure()
        }
        $mutations += @{ Name = "$label $from"; Change = $change }
    }
}
$count = 0
try {
    foreach ($side in @('symbols', 'main')) {
        if (Test-Path -LiteralPath $mainFixture) { Remove-Item -LiteralPath $mainFixture }
        $payload = if ($side -eq 'symbols') { $symbolPath } else { '' }
        if ($side -eq 'symbols') {
            # Preserve the actual main package read-only; do not reconstruct its image payload.
            $null = New-Item -ItemType HardLink -Path $mainFixture -Target ([IO.Path]::GetFullPath($PackagePath))
        }
        else { Write-NuspecArchive $mainFixture $mainText }
        Write-NuspecArchive $symbolFixture $symbolText $false $payload
        $null = & $validator -PackagePath $mainFixture -SymbolPackagePath $symbolFixture
        foreach ($validChange in @(
            { param($s) $s.Replace('aspnetcore', 'aspnetco&#114;e') },
            { param($s)
                $text = [regex]::Match($s, '<tags>([^<]*)</tags>').Groups[1].Value
                $tags = [string[]]@($text -split ' ' | Where-Object { $_ })
                [array]::Reverse($tags)
                $s.Replace($text, ($tags -join ",; `t"))
            }
        )) {
            $text = & $validChange $(if ($side -eq 'main') { $mainText } else { $symbolText })
            $path = if ($side -eq 'main') { $mainFixture } else { $symbolFixture }
            Write-NuspecArchive $path $text $false $payload
            $null = & $validator -PackagePath $mainFixture -SymbolPackagePath $symbolFixture
            if ($side -eq 'symbols') { $null = & $inspector -Version $Version -PackagePath $mainFixture -AllowMissingIcon }
        }
        foreach ($mutation in $mutations) {
            $original = if ($side -eq 'main') { $mainText } else { $symbolText }
            $changed = & $mutation.Change $original
            if ([string]::Equals($changed, $original, [StringComparison]::Ordinal) -and -not $mutation.DuplicateRoot) { throw "Archive mutation did not apply: $($mutation.Name)." }
            $path = if ($side -eq 'main') { $mainFixture } else { $symbolFixture }
            Write-NuspecArchive $path $changed ([bool]$mutation.DuplicateRoot) $payload
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
        $text = if ($side -eq 'main') { $mainText } else { $symbolText }
        $nativeCases = @(@{ Text = $text.Replace('keelmatrix-primary', 'KEELMATRIX-PRIMARY'); Error = 'Website classification tags' })
        foreach ($codePoint in @(0x200B, 0x00AD, 0x2060)) {
            $nativeCases += @{ Text = $text.Replace('url="https://github.com/KeelMatrix/KeyRingGuard"', ('url="https://github.com/KeelMatrix/KeyRing' + [char]$codePoint + 'Guard"')); Error = 'Website metadata requires canonical' }
        }
        foreach ($invalid in $nativeCases) {
            if ($side -eq 'main') { Write-NuspecArchive $mainFixture $mainText }
            Write-NuspecArchive $symbolFixture $symbolText $false $payload
            $path = if ($side -eq 'main') { $mainFixture } else { $symbolFixture }
            Write-NuspecArchive $path $invalid.Text $false $payload
            $arguments = @('-Version', $Version, '-PackagePath', $mainFixture, '-AllowMissingIcon')
            $output = @(Invoke-NestedPwsh -NoProfile -File $inspector @arguments 2>&1)
            if ($LASTEXITCODE -eq 0 -or ($output -join ' ') -notmatch $invalid.Error) { throw "Native inspector bypassed $side metadata." }
            $argumentsPath = Join-Path $scratch 'arguments.json'
            [IO.File]::WriteAllText($argumentsPath, (ConvertTo-Json -InputObject $arguments))
            $marker = Join-Path $scratch "following-$side"
            $null = @(Invoke-NestedPwsh -NoProfile -File (Join-Path $PSScriptRoot 'Invoke-GateFailureProbe.ps1') -CommandFile $inspector -ArgumentsPath $argumentsPath -MarkerPath $marker 2>&1)
            if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $marker)) { throw "Invalid $side metadata advanced to a following operation." }
        }
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
    if (-not ([IO.Path]::GetFullPath($scratch)).StartsWith($fixtureRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal)) { throw 'Unsafe archive fixture cleanup path.' }
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
