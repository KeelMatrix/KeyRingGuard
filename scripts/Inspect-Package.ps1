[CmdletBinding()]
param(
    [string]$PackagePath = (Join-Path $PSScriptRoot '..\artifacts\packages\KeelMatrix.KeyRingGuard.0.1.0.nupkg'),
    [string]$ExpectedPayloadPath = (Join-Path $PSScriptRoot 'ExpectedPackagePayload.txt')
)

$ErrorActionPreference = 'Stop'
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$expected = @(Get-Content -LiteralPath $ExpectedPayloadPath | Where-Object { $_ -and -not $_.StartsWith('#') } | Sort-Object)

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
    if ($metadata.id -ne 'KeelMatrix.KeyRingGuard' -or $metadata.version -ne '0.1.0') { throw 'Package identity or version is incorrect.' }
    if ($metadata.readme -ne 'README.md' -or $metadata.license.type -ne 'file' -or $metadata.license.InnerText -ne 'LICENSE') { throw 'README or license metadata is incorrect.' }
    if (-not $metadata.dependencies.group.dependency) { throw 'The package has no declared Data Protection dependency.' }
    $dependencies = @($metadata.dependencies.group.dependency | ForEach-Object id)
    if ($dependencies -contains 'KeelMatrix.Telemetry' -or $dependencies -match 'Redis|Azure|AWS|StackExchange|SqlClient') { throw "Unexpected provider or telemetry dependency: $($dependencies -join ', ')." }

    $forbidden = $actual | Where-Object { $_ -match '(?i)(\.env|telemetry|research|\.pfx$|\.p12$|\.pem$|secret|credential)' }
    if ($forbidden) { throw "Forbidden package content found: $($forbidden -join ', ')." }

    Write-Output "Package inspection: PASS ($package)"
    Write-Output "Package payload entries: $($actual.Count)"
    Write-Output "Package icon metadata: $($metadata.icon)"
} finally {
    $archive.Dispose()
}

$project = (Resolve-Path (Join-Path $PSScriptRoot '..\src\KeelMatrix.KeyRingGuard\KeelMatrix.KeyRingGuard.csproj')).Path
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$projectText = Get-Content -Raw -LiteralPath $project
Write-Output 'Icon pack configuration:'
$projectText -split "`r?`n" | Where-Object { $_ -match 'PackageIcon|icon\.png' } | ForEach-Object { Write-Output $_.Trim() }
Write-Output "Resolved icon source path: $([System.IO.Path]::Combine($repositoryRoot, 'icon.png'))"
if (Test-Path -LiteralPath (Join-Path $repositoryRoot 'icon.png')) {
    Write-Output 'Icon source exists; inspect the package metadata and embedded file before release.'
} else {
    Write-Output 'Icon source is not present in this candidate; package metadata omits icon.png.'
}
