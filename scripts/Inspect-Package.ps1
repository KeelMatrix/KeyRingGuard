[CmdletBinding()]
param(
    [string]$Version = $env:RELEASE_VERSION,
    [string]$PackagePath = '',
    [string]$ExpectedPayloadPath = (Join-Path $PSScriptRoot 'ExpectedPackagePayload.txt'),
    [switch]$AllowMissingIcon
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = '0.1.0' }
if ([string]::IsNullOrWhiteSpace($PackagePath)) {
    $PackagePath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..' 'artifacts' 'packages' "KeelMatrix.KeyRingGuard.$Version.nupkg"))
}
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$iconSource = Join-Path $repositoryRoot 'icon.png'
$projectReadmePath = Join-Path $repositoryRoot 'src' 'KeelMatrix.KeyRingGuard' 'README.md'
$iconSourcePresent = Test-Path -LiteralPath $iconSource -PathType Leaf
if (-not $iconSourcePresent -and -not $AllowMissingIcon) {
    throw "Icon contract failed: required repository-root icon.png is missing: $iconSource"
}
$expected = @(Get-Content -LiteralPath $ExpectedPayloadPath |
    Where-Object { $_ -and -not $_.StartsWith('#') -and ($iconSourcePresent -or $_ -ne 'icon.png') } |
    Sort-Object)

function Assert-IconBytes([byte[]]$Bytes, [string]$Description) {
    if ($Bytes.Length -gt 200KB) { throw "Icon contract failed: $Description is larger than 200 KB." }
    if ($Bytes.Length -lt 33 -or -not [System.Linq.Enumerable]::SequenceEqual(
            $Bytes[0..7],
            [byte[]](137, 80, 78, 71, 13, 10, 26, 10))) {
        throw "Icon contract failed: $Description is not a PNG file."
    }
    if ([Text.Encoding]::ASCII.GetString($Bytes, 12, 4) -cne 'IHDR') {
        throw "Icon contract failed: $Description has no PNG IHDR chunk."
    }

    $width = ([uint32]$Bytes[16] -shl 24) -bor ([uint32]$Bytes[17] -shl 16) -bor ([uint32]$Bytes[18] -shl 8) -bor $Bytes[19]
    $height = ([uint32]$Bytes[20] -shl 24) -bor ([uint32]$Bytes[21] -shl 16) -bor ([uint32]$Bytes[22] -shl 8) -bor $Bytes[23]
    if ($width -ne 512 -or $height -ne 512) {
        throw "Icon contract failed: $Description must be exactly 512x512 pixels (actual ${width}x${height})."
    }
}

$sourceBytes = $null
if ($iconSourcePresent) {
    $sourceBytes = [IO.File]::ReadAllBytes($iconSource)
    Assert-IconBytes $sourceBytes 'required repository-root icon.png'
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($package)
try {
    $actual = @($archive.Entries | ForEach-Object FullName | Sort-Object)
    $missing = Compare-Object -ReferenceObject $expected -DifferenceObject $actual -PassThru | Where-Object SideIndicator -eq '<='
    $unexpected = Compare-Object -ReferenceObject $expected -DifferenceObject $actual -PassThru | Where-Object SideIndicator -eq '=>'
    if ($missing -or $unexpected) {
        throw "Package payload mismatch. Missing: $($missing -join ', '). Unexpected: $($unexpected -join ', ')."
    }

    $nuspecEntry = $archive.GetEntry('KeelMatrix.KeyRingGuard.nuspec')
    $reader = [System.IO.StreamReader]::new($nuspecEntry.Open())
    try { [xml]$nuspec = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $namespace = [System.Xml.XmlNamespaceManager]::new($nuspec.NameTable)
    $namespace.AddNamespace('n', 'http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd')
    $metadata = $nuspec.SelectSingleNode('/n:package/n:metadata', $namespace)
    if ($metadata.id -ne 'KeelMatrix.KeyRingGuard' -or $metadata.version -ne $Version) { throw 'Package identity or version is incorrect.' }
    if ($metadata.readme -ne 'README.md' -or $metadata.license.type -ne 'file' -or $metadata.license.InnerText -ne 'LICENSE') { throw 'README or license metadata is incorrect.' }
    $readmeEntry = $archive.GetEntry('README.md')
    $readmeReader = [System.IO.StreamReader]::new($readmeEntry.Open())
    try { $packedReadme = $readmeReader.ReadToEnd() } finally { $readmeReader.Dispose() }
    $projectReadme = [IO.File]::ReadAllText($projectReadmePath)
    if (-not [string]::Equals($packedReadme, $projectReadme, [StringComparison]::Ordinal)) {
        throw 'Package README differs from the project-local README that is declared as the package input.'
    }
    $iconEntry = $archive.GetEntry('icon.png')
    if ($iconSourcePresent) {
        if ($metadata.icon -cne 'icon.png') { throw "Icon contract failed: nuspec <icon> must be 'icon.png'." }
        if ($null -eq $iconEntry) { throw 'Icon contract failed: package-root icon.png is missing.' }
        $iconStream = $iconEntry.Open()
        try {
            $embeddedBytes = [byte[]]::new($iconEntry.Length)
            $offset = 0
            while ($offset -lt $embeddedBytes.Length) {
                $read = $iconStream.Read($embeddedBytes, $offset, $embeddedBytes.Length - $offset)
                if ($read -le 0) { break }
                $offset += $read
            }
        } finally { $iconStream.Dispose() }
        if ($offset -ne $embeddedBytes.Length) { throw 'Icon contract failed: package-root icon.png could not be read completely.' }
        Assert-IconBytes $embeddedBytes 'package-root icon.png'
        $sourceHash = [Security.Cryptography.SHA256]::HashData($sourceBytes)
        $embeddedHash = [Security.Cryptography.SHA256]::HashData($embeddedBytes)
        if (-not [Linq.Enumerable]::SequenceEqual($sourceHash, $embeddedHash)) { throw 'Icon contract failed: embedded icon.png differs from repository-root icon.png.' }
    } elseif ($metadata.icon -or $iconEntry) {
        throw 'Icon contract failed: package contains icon metadata or bytes while the repository-root icon.png is absent.'
    }
    if (-not $metadata.dependencies.group.dependency) { throw 'The package has no declared Data Protection dependency.' }
    $dependencies = @($metadata.dependencies.group.dependency | ForEach-Object id)
    if ($dependencies -contains 'KeelMatrix.Telemetry' -or $dependencies -match 'Redis|Azure|AWS|StackExchange|SqlClient') { throw "Unexpected provider or telemetry dependency: $($dependencies -join ', ')." }

    $forbidden = $actual | Where-Object { $_ -match '(?i)([.]env|telemetry|research|[.]pfx$|[.]p12$|[.]pem$|secret|credential)' }
    if ($forbidden) { throw "Forbidden package content found: $($forbidden -join ', ')." }

    Write-Output "Package inspection: PASS ($package)"
    Write-Output "Package payload entries: $($actual.Count)"
    if ($iconSourcePresent) {
        Write-Output "Package icon metadata: $($metadata.icon)"
    } else {
        Write-Output 'Package icon metadata: absent (allowed for readiness validation)'
    }
} finally {
    $archive.Dispose()
}

$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..' 'src' 'KeelMatrix.KeyRingGuard' 'KeelMatrix.KeyRingGuard.csproj'))
$symbolPackage = [System.IO.Path]::ChangeExtension($package, '.snupkg')
if (-not (Test-Path -LiteralPath $symbolPackage)) { throw "Symbol package is missing: $symbolPackage" }
$symbolArchive = [System.IO.Compression.ZipFile]::OpenRead($symbolPackage)
try {
    $symbolEntries = @($symbolArchive.Entries | ForEach-Object FullName | Sort-Object)
    $expectedSymbols = @('_rels/.rels', 'KeelMatrix.KeyRingGuard.nuspec', 'lib/net8.0/KeelMatrix.KeyRingGuard.pdb', '[Content_Types].xml', 'package/services/metadata/core-properties/nuget.psmdcp' | Sort-Object)
    if ((Compare-Object -ReferenceObject $expectedSymbols -DifferenceObject $symbolEntries)) { throw 'Symbol package payload mismatch.' }
    Write-Output "Symbol package inspection: PASS ($symbolPackage)"
} finally {
    $symbolArchive.Dispose()
}

$projectText = Get-Content -Raw -LiteralPath $project
Write-Output 'Icon pack configuration:'
$projectText -split "`r?`n" | Where-Object { $_ -match 'PackageIcon|icon[.]png' } | ForEach-Object { Write-Output $_.Trim() }
Write-Output "Resolved icon source path: $([System.IO.Path]::Combine($repositoryRoot, 'icon.png'))"
if ($iconSourcePresent) {
    Write-Output 'Icon contract: PASS (source and package-root icon.png are valid and byte-identical)'
} else {
    Write-Output 'Icon contract: PASS (icon absent under explicit readiness allowance)'
}
