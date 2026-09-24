[CmdletBinding()]
param(
    [string]$PackagePath = (Join-Path $PSScriptRoot '..' 'artifacts' 'packages' 'KeelMatrix.KeyRingGuard.0.1.0.nupkg'),
    [switch]$SourceOnly
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$iconPath = Join-Path $root 'icon.png'

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

if (-not (Test-Path -LiteralPath $iconPath -PathType Leaf)) {
    throw "Icon contract failed: required repository-root icon.png is missing: $iconPath"
}
$sourceBytes = [IO.File]::ReadAllBytes($iconPath)
Assert-IconBytes $sourceBytes 'required repository-root icon.png'

if ($SourceOnly) {
    Write-Output "Icon source contract: PASS ($iconPath)"
    return
}

if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
    throw "Icon contract failed: package '$PackagePath' does not exist."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $PackagePath).Path)
try {
    $entry = $archive.GetEntry('icon.png')
    if ($null -eq $entry) { throw 'Icon contract failed: package-root icon.png is missing.' }
    $nuspec = $archive.GetEntry('KeelMatrix.KeyRingGuard.nuspec')
    if ($null -eq $nuspec) { throw 'Icon contract failed: package nuspec is missing.' }
    $reader = [IO.StreamReader]::new($nuspec.Open())
    try { [xml]$document = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $namespace = [Xml.XmlNamespaceManager]::new($document.NameTable)
    $namespace.AddNamespace('n', 'http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd')
    $iconMetadata = $document.SelectSingleNode('/n:package/n:metadata/n:icon', $namespace)
    if ($null -eq $iconMetadata -or $iconMetadata.InnerText -cne 'icon.png') {
        throw "Icon contract failed: nuspec <icon> must be 'icon.png'."
    }

    $stream = $entry.Open()
    try {
        $embeddedBytes = [byte[]]::new($entry.Length)
        $offset = 0
        while ($offset -lt $embeddedBytes.Length) {
            $read = $stream.Read($embeddedBytes, $offset, $embeddedBytes.Length - $offset)
            if ($read -le 0) { break }
            $offset += $read
        }
    } finally { $stream.Dispose() }
    if ($offset -ne $embeddedBytes.Length) { throw 'Icon contract failed: package-root icon.png could not be read completely.' }
    Assert-IconBytes $embeddedBytes 'package-root icon.png'
    if (-not [Linq.Enumerable]::SequenceEqual(
            [Security.Cryptography.SHA256]::HashData($sourceBytes),
            [Security.Cryptography.SHA256]::HashData($embeddedBytes))) {
        throw 'Icon contract failed: embedded icon.png differs from repository-root icon.png.'
    }
}
finally {
    $archive.Dispose()
}

Write-Output "Icon contract: PASS ($iconPath -> package-root icon.png)"
