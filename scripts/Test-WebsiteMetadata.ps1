[CmdletBinding()]
param(
    [string]$RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')),
    [string]$PackagePath = ''
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../build/WebsiteMetadata.ps1')
. (Join-Path $PSScriptRoot 'Invoke-BoundedDotnet.ps1')
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$manifestPath = Join-Path $RepositoryRoot 'keelmatrix.website.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Website classification manifest is missing.' }
$manifest = [IO.File]::ReadAllText($manifestPath, [Text.UTF8Encoding]::new($false, $true))
Push-Location $RepositoryRoot
try {
    # Validate the selected SDK before evaluating inherited Release properties.
    $null = & (Join-Path $PSScriptRoot 'Test-SdkSelection.ps1')
    $projects = Get-ChildItem -LiteralPath $RepositoryRoot -Recurse -Filter '*.csproj' | Where-Object {
        [IO.Path]::GetRelativePath($RepositoryRoot, $_.FullName).Replace([char]92, '/') -notmatch '(^|/)(bin|obj|artifacts|[.]git)(/|$)'
    }
    $packages = @(foreach ($project in $projects) {
        $result = Invoke-BoundedDotnet -Arguments @(
            'msbuild', $project.FullName, '-nologo', '-p:Configuration=Release',
            '-getProperty:IsPackable,PackageId,PackageTags,PackageProjectUrl,RepositoryUrl,RepositoryType')
        $properties = ($result -join "`n" | ConvertFrom-Json).Properties
        if ($properties.IsPackable -ieq 'true') { $properties }
    })
}
finally { Pop-Location }

$packed = $null
if ($PackagePath) {
    $archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $PackagePath).Path)
    try {
        $specs = @($archive.Entries | Where-Object { $_.FullName -cmatch '^[^/]+[.]nuspec$' })
        if ($specs.Count -ne 1) { throw 'Built package must contain exactly one root nuspec.' }
        $settings = [Xml.XmlReaderSettings]::new()
        $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $stream = $specs[0].Open()
        $reader = [Xml.XmlReader]::Create($stream, $settings)
        try {
            $xml = [Xml.XmlDocument]::new()
            $xml.Load($reader)
        }
        finally { $reader.Dispose(); $stream.Dispose() }
        $metadata = $xml.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
        $packed = [pscustomobject]@{}
        foreach ($pair in @(
            @('PackageId', 'id'), @('PackageTags', 'tags'), @('PackageProjectUrl', 'projectUrl')
        )) {
            $nodes = @($metadata.SelectNodes("*[local-name()='$($pair[1])']"))
            if ($nodes.Count -ne 1) { throw "Built package requires exactly one $($pair[1]) metadata element." }
            $packed | Add-Member -NotePropertyName $pair[0] -NotePropertyValue $nodes[0].InnerText
        }
        $repositories = @($metadata.SelectNodes('*[local-name()="repository"]'))
        if ($repositories.Count -ne 1) { throw 'Built package requires exactly one repository element.' }
        $packed | Add-Member -NotePropertyName RepositoryUrl -NotePropertyValue $repositories[0].GetAttribute('url')
        $packed | Add-Member -NotePropertyName RepositoryType -NotePropertyValue $repositories[0].GetAttribute('type')
    }
    finally { $archive.Dispose() }
}
Assert-WebsiteMetadata $manifest $packages $packed
Write-Output 'Website metadata: PASS (manifest, evaluated Release source and supplied package agree).'
