function Read-PackageSourceLink([string]$SymbolPackage) {
    Add-Type -AssemblyName System.Reflection.Metadata
    $archive = [IO.Compression.ZipFile]::OpenRead($SymbolPackage)
    try {
        $entry = $archive.GetEntry('lib/net8.0/KeelMatrix.KeyRingGuard.pdb')
        if ($null -eq $entry) { throw 'SourceLink contract: shipping PDB is missing.' }
        $stream = [IO.MemoryStream]::new()
        $inputStream = $entry.Open()
        try { $inputStream.CopyTo($stream) } finally { $inputStream.Dispose() }
        $stream.Position = 0
        $provider = [Reflection.Metadata.MetadataReaderProvider]::FromPortablePdbStream($stream)
        try {
            $reader = $provider.GetMetadataReader()
            $sourceLink = $null
            $embedded = @{}
            foreach ($handle in $reader.CustomDebugInformation) {
                $record = $reader.GetCustomDebugInformation($handle)
                $kind = $reader.GetGuid($record.Kind)
                if ($kind -eq [guid]'cc110556-a091-4d38-9fec-25ab9a351a6a') {
                    $sourceLink = [Text.Encoding]::UTF8.GetString($reader.GetBlobBytes($record.Value)) | ConvertFrom-Json -AsHashtable
                }
                if ($kind -eq [guid]'0e8a571b-6926-466e-b4ad-8ab04611f5fe') {
                    $document = $reader.GetDocument([Reflection.Metadata.DocumentHandle]$record.Parent)
                    $blob = $reader.GetBlobBytes($record.Value)
                    $size = [BitConverter]::ToInt32($blob, 0)
                    $data = [IO.MemoryStream]::new($blob, 4, $blob.Length - 4)
                    try {
                        if ($size -eq 0) { $bytes = $blob[4..($blob.Length - 1)] }
                        else {
                            $inflate = [IO.Compression.DeflateStream]::new($data, [IO.Compression.CompressionMode]::Decompress)
                            $outputStream = [IO.MemoryStream]::new()
                            try { $inflate.CopyTo($outputStream); $bytes = $outputStream.ToArray() }
                            finally { $inflate.Dispose(); $outputStream.Dispose() }
                            if ($bytes.Length -ne $size) { throw 'SourceLink contract: invalid embedded source length.' }
                        }
                        $embedded[$reader.GetString($document.Name)] = [byte[]]$bytes
                    } finally { $data.Dispose() }
                }
            }
            $documents = foreach ($handle in $reader.Documents) {
                $document = $reader.GetDocument($handle)
                $name = $reader.GetString($document.Name)
                [pscustomobject]@{
                    Name = $name
                    Algorithm = $reader.GetGuid($document.HashAlgorithm)
                    Checksum = [Convert]::ToHexString($reader.GetBlobBytes($document.Hash))
                    Embedded = $embedded.ContainsKey($name)
                    Bytes = $embedded[$name]
                }
            }
            return [pscustomobject]@{ SourceLink = $sourceLink; Documents = @($documents) }
        } finally { $provider.Dispose(); $stream.Dispose() }
    } finally { $archive.Dispose() }
}

function Assert-SourceLinkDocuments($Data, [string]$RepositoryRoot, [string]$Commit) {
    if ($Commit -cnotmatch '^[a-f0-9]{40}$') { throw 'SourceLink contract: invalid repository commit.' }
    if (-not $Data.SourceLink.documents -or $Data.Documents.Count -eq 0) { throw 'SourceLink contract: source metadata is missing.' }
    $tree = @(git -C $RepositoryRoot ls-tree -r --name-only $Commit)
    if ($LASTEXITCODE -ne 0) { throw 'SourceLink contract: cannot read the recorded Git tree.' }
    $expected = @($tree | Where-Object { $_ -clike 'src/KeelMatrix.KeyRingGuard/*.cs' })
    $resolved = [Collections.Generic.List[string]]::new()
    $urls = [Collections.Generic.List[string]]::new()
    $embeddedCount = 0
    $remotePrefix = "https://raw.githubusercontent.com/KeelMatrix/KeyRingGuard/$Commit/"
    foreach ($document in $Data.Documents) {
        if (-not $document.Name.StartsWith('/_/', [StringComparison]::Ordinal) -or
            $document.Name.Contains([char]92) -or $document.Name.Contains(':') -or $document.Name.Contains('/../')) {
            throw 'SourceLink contract: PDB document contains a private or non-deterministic path.'
        }
        if ($document.Embedded) {
            if ($document.Name -cnotmatch '^/_/src/KeelMatrix[.]KeyRingGuard/obj/Release/net8[.]0/[^/]+[.]cs$') {
                throw 'SourceLink contract: only generated shipping sources may be embedded.'
            }
            $bytes = [byte[]]$document.Bytes
            $embeddedCount++
        } else {
            $matches = @($Data.SourceLink.documents.Keys | Where-Object {
                    $_.EndsWith('*') -and $document.Name.StartsWith($_.TrimEnd('*'), [StringComparison]::Ordinal)
                })
            if ($matches.Count -ne 1) { throw 'SourceLink contract: document must have one source mapping.' }
            $key = $matches[0]
            $pattern = $Data.SourceLink.documents[$key]
            if (-not $pattern.EndsWith('*')) { throw 'SourceLink contract: invalid source URL pattern.' }
            $url = $pattern.TrimEnd('*') + $document.Name.Substring($key.Length - 1)
            if (-not $url.StartsWith($remotePrefix, [StringComparison]::Ordinal)) {
                throw 'SourceLink contract: source URL does not use the recorded repository commit.'
            }
            $path = $url.Substring($remotePrefix.Length)
            if ($path -cnotin $expected) { throw "SourceLink contract: mapped source is absent from the shipping Git tree: $path" }
            $physicalPath = Join-Path $RepositoryRoot $path
            $blob = (git -C $RepositoryRoot rev-parse "${Commit}:$path") -join ''
            if ($LASTEXITCODE -ne 0) { throw 'SourceLink contract: cannot read source blob identity.' }
            $workingBlob = (git -C $RepositoryRoot hash-object --no-filters -- $physicalPath) -join ''
            if ($LASTEXITCODE -ne 0 -or $workingBlob -cne $blob) { throw 'SourceLink contract: source bytes differ from the recorded Git tree.' }
            $bytes = [IO.File]::ReadAllBytes($physicalPath)
            $resolved.Add($path)
            $urls.Add($url)
        }
        $hash = switch ($document.Algorithm.ToString()) {
            '8829d00f-11b8-4213-878b-770e8597ac16' { [Security.Cryptography.SHA256]::HashData($bytes); break }
            'ff1816ec-aa5e-4d10-87f7-6f4963833460' { [Security.Cryptography.SHA1]::HashData($bytes); break }
            default { throw 'SourceLink contract: unsupported document checksum algorithm.' }
        }
        if ([Convert]::ToHexString([byte[]]$hash) -cne $document.Checksum) { throw 'SourceLink contract: document checksum mismatch.' }
    }
    if ($embeddedCount -eq 0 -or $resolved.Count -ne $expected.Count -or
        @($resolved | Sort-Object -Unique).Count -ne $expected.Count -or
        (Compare-Object $expected $resolved)) { throw 'SourceLink contract: shipping source set is incomplete.' }
    [pscustomobject]@{ Tracked = $resolved.Count; Embedded = $embeddedCount; Urls = $urls.ToArray() }
}
