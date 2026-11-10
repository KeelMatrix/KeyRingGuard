[CmdletBinding()]
param(
    [string]$RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')),
    [string]$PackagePath = '',
    [string]$SymbolPackagePath = ''
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
    $projects = Get-ChildItem -LiteralPath $RepositoryRoot -Recurse -File -Force | Where-Object {
        $_.Extension -iin @('.csproj', '.vbproj', '.fsproj') -and
        [IO.Path]::GetRelativePath($RepositoryRoot, $_.FullName).Replace([char]92, '/') -notmatch '(^|/)(bin|obj|artifacts|[.]git)(/|$)'
    }
    $packages = @(foreach ($project in $projects) {
        $result = Invoke-BoundedDotnet -Arguments @(
            'msbuild', $project.FullName, '-nologo', '-p:Configuration=Release',
            '-getProperty:IsPackable,PackageId,PackageTags,PackageProjectUrl,RepositoryUrl,RepositoryType')
        $properties = ($result -join "`n" | ConvertFrom-Json).Properties
        if ($properties.IsPackable -ieq 'true') {
            $relative = [IO.Path]::GetRelativePath($RepositoryRoot, $project.FullName).Replace([char]92, '/')
            if ($relative -cne 'src/KeelMatrix.KeyRingGuard/KeelMatrix.KeyRingGuard.csproj') {
                throw "Only the intended shipping project may be packable: $relative."
            }
            $properties
        }
    })
}
finally { Pop-Location }

Assert-WebsiteMetadata $manifest $packages
foreach ($path in @($PackagePath, $SymbolPackagePath)) {
    if ($path) { Assert-WebsiteMetadata $manifest $packages (Read-WebsitePackageMetadata $path) }
}
Write-Output 'Website metadata: PASS (manifest, single shipping project, evaluated Release source and supplied archives agree).'
