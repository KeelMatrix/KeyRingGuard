function Read-WebsitePackageMetadata([string]$Path) {
    $archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Path).Path)
    try {
        $specs = @($archive.Entries | Where-Object { $_.FullName -imatch '^[^/]+[.]nuspec$' })
        if ($specs.Count -ne 1) { throw 'Built package must contain exactly one root nuspec.' }
        $settings = [Xml.XmlReaderSettings]::new()
        $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $stream = $specs[0].Open()
        $reader = [Xml.XmlReader]::Create($stream, $settings)
        try {
            $xml = [Xml.XmlDocument]::new()
            try { $xml.Load($reader) } catch { throw 'Built package nuspec must be well-formed XML without a DTD.' }
        }
        finally { $reader.Dispose(); $stream.Dispose() }
        if ($xml.DocumentElement.LocalName -cne 'package') { throw 'Built package requires a package root.' }
        $metadataNodes = @($xml.DocumentElement.SelectNodes('*[local-name()="metadata"]'))
        if ($metadataNodes.Count -ne 1) { throw 'Built package requires exactly one metadata element.' }
        $metadata = $metadataNodes[0]
        $packed = [pscustomobject]@{}
        foreach ($pair in @(
            @('PackageId', 'id'), @('PackageTags', 'tags'), @('PackageProjectUrl', 'projectUrl')
        )) {
            $nodes = @($metadata.SelectNodes("*[local-name()='$($pair[1])']"))
            if ($nodes.Count -ne 1 -or $nodes[0].SelectNodes('*').Count -ne 0) {
                throw "Built package requires exactly one text-only $($pair[1]) metadata element."
            }
            $packed | Add-Member -NotePropertyName $pair[0] -NotePropertyValue $nodes[0].InnerText
        }
        $repositories = @($metadata.SelectNodes('*[local-name()="repository"]'))
        if ($repositories.Count -ne 1 -or $repositories[0].SelectNodes('*').Count -ne 0) {
            throw 'Built package requires exactly one repository element without child elements.'
        }
        $packed | Add-Member -NotePropertyName RepositoryUrl -NotePropertyValue $repositories[0].GetAttribute('url')
        $packed | Add-Member -NotePropertyName RepositoryType -NotePropertyValue $repositories[0].GetAttribute('type')
        return $packed
    }
    finally { $archive.Dispose() }
}

function Assert-WebsiteObject($Element, [string[]]$Names) {
    if ($Element.ValueKind -ne [Text.Json.JsonValueKind]::Object) { throw 'Website metadata must be an object.' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($property in $Element.EnumerateObject()) {
        if ($Names -cnotcontains $property.Name -or -not $seen.Add($property.Name)) {
            throw "Unknown or duplicate website metadata property: $($property.Name)."
        }
    }
    if ($seen.Count -ne $Names.Count) { throw 'Website metadata is missing a required property.' }
}

function Get-WebsiteTags([string]$Tags) {
    $separators = '[;,' + [char]92 + 's]+'
    return @($Tags -split $separators | Where-Object { $_ })
}

function Assert-WebsiteClassification($Package, [string]$Visibility, [string]$Role) {
    $tags = @(Get-WebsiteTags $Package.PackageTags)
    $visibilityTags = @($tags | Where-Object { $_ -cmatch '^keelmatrix-(public-product|internal-package)$' })
    $roleTags = @($tags | Where-Object { $_ -cmatch '^keelmatrix-(primary|component)$' })
    $unknown = @($tags | Where-Object {
        $_ -imatch '^keelmatrix-' -and $_ -cnotin @(
            'keelmatrix-public-product', 'keelmatrix-internal-package', 'keelmatrix-primary', 'keelmatrix-component')
    })
    if ($unknown.Count -or $visibilityTags.Count -ne 1 -or $roleTags.Count -ne 1 -or
        $visibilityTags[0] -cne "keelmatrix-$Visibility" -or $roleTags[0] -cne "keelmatrix-$Role") {
        throw "Website classification tags are missing, duplicated, conflicting or incorrect for $($Package.PackageId)."
    }
    $canonical = 'https://github.com/KeelMatrix/KeyRingGuard'
    if ($Package.PackageProjectUrl -cnotin @($canonical, "$canonical#readme") -or
        $Package.RepositoryUrl -cne $canonical -or $Package.RepositoryType -cne 'git') {
        throw "Website metadata requires canonical KeyRingGuard project and git repository URLs for $($Package.PackageId)."
    }
}

function Assert-WebsiteMetadata([string]$ManifestText, [object[]]$Packages, $PackedPackage = $null) {
    $document = [Text.Json.JsonDocument]::Parse($ManifestText)
    try {
        $manifest = $document.RootElement
        Assert-WebsiteObject $manifest @('schemaVersion', 'packages')
        $schema = $manifest.GetProperty('schemaVersion')
        if ($schema.ValueKind -ne [Text.Json.JsonValueKind]::Number -or $schema.GetRawText() -cne '1') {
            throw 'Website metadata requires schemaVersion 1.'
        }
        $entries = $manifest.GetProperty('packages')
        if ($entries.ValueKind -ne [Text.Json.JsonValueKind]::Object) { throw 'Website packages must be an object.' }
        $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $primaryCount = 0
        $publicCount = 0
        foreach ($entry in $entries.EnumerateObject()) {
            if (-not $ids.Add($entry.Name)) { throw "Duplicate website package ID: $($entry.Name)." }
            Assert-WebsiteObject $entry.Value @('visibility', 'role')
            $visibility = $entry.Value.GetProperty('visibility')
            $role = $entry.Value.GetProperty('role')
            if ($visibility.ValueKind -ne [Text.Json.JsonValueKind]::String -or
                $visibility.GetString() -cnotin @('public-product', 'internal-package') -or
                $role.ValueKind -ne [Text.Json.JsonValueKind]::String -or
                $role.GetString() -cnotin @('primary', 'component')) {
                throw "Invalid website visibility or role for $($entry.Name)."
            }
            if ($visibility.GetString() -ceq 'public-product') { $publicCount++ }
            if ($role.GetString() -ceq 'primary') { $primaryCount++ }
            $source = @($Packages | Where-Object { $_.PackageId -ceq $entry.Name })
            if ($source.Count -ne 1) { throw "Website package ID must match exactly one packable project: $($entry.Name)." }
            Assert-WebsiteClassification $source[0] $visibility.GetString() $role.GetString()
        }
        if ($ids.Count -eq 0 -or $ids.Count -ne $Packages.Count -or
            @($Packages | Where-Object { $_.PackageId -ceq 'KeelMatrix.KeyRingGuard' }).Count -ne 1) {
            throw 'Website manifest must name every packable package, including KeelMatrix.KeyRingGuard, exactly once.'
        }
        if ($primaryCount -gt 1 -or ($publicCount -gt 0 -and $primaryCount -ne 1)) {
            throw 'A public product family must have exactly one primary package.'
        }
        # This repository ships a direct developer-facing product.
        $product = $entries.GetProperty('KeelMatrix.KeyRingGuard')
        if ($product.GetProperty('visibility').GetString() -cne 'public-product' -or
            $product.GetProperty('role').GetString() -cne 'primary') {
            throw 'KeelMatrix.KeyRingGuard must be public-product/primary.'
        }
        if ($null -ne $PackedPackage) {
            $source = @($Packages | Where-Object { $_.PackageId -ceq $PackedPackage.PackageId })
            if ($source.Count -ne 1) { throw 'Built package ID does not match a packable project.' }
            $entry = $entries.GetProperty($PackedPackage.PackageId)
            Assert-WebsiteClassification $PackedPackage $entry.GetProperty('visibility').GetString() $entry.GetProperty('role').GetString()
            $expectedTags = @(Get-WebsiteTags $source[0].PackageTags | Sort-Object -CaseSensitive)
            $actualTags = @(Get-WebsiteTags $PackedPackage.PackageTags | Sort-Object -CaseSensitive)
            if (($expectedTags -join ';') -cne ($actualTags -join ';') -or
                $source[0].PackageProjectUrl -cne $PackedPackage.PackageProjectUrl) {
                throw 'Built package website metadata differs from evaluated source metadata.'
            }
        }
    }
    finally { $document.Dispose() }
}
