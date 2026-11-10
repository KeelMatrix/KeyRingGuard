[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../build/WebsiteMetadata.ps1')
. (Join-Path $PSScriptRoot '../build/Invoke-NestedPwsh.ps1')
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$manifest = '{"schemaVersion":1,"packages":{"KeelMatrix.KeyRingGuard":{"visibility":"public-product","role":"primary"}}}'
$source = [pscustomobject]@{
    PackageId = 'KeelMatrix.KeyRingGuard'
    PackageTags = 'aspnetcore;testing;keelmatrix-public-product;keelmatrix-primary'
    PackageProjectUrl = 'https://github.com/KeelMatrix/KeyRingGuard#readme'
    RepositoryUrl = 'https://github.com/KeelMatrix/KeyRingGuard'
    RepositoryType = 'git'
}
$count = 0
function Assert-Rejected([scriptblock]$Action, [string]$Name) {
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    if (-not $rejected) { throw "Website metadata accepted invalid $Name." }
    $script:count++
}
Assert-WebsiteMetadata $manifest @($source) $source
foreach ($text in @(
    '', '{}', 'null', '[]', ($manifest + ' trailing'),
    $manifest.Replace('"schemaVersion":1,', '"schemaVersion":1,/* comment */'),
    $manifest.Replace('"schemaVersion":1,', '"schemaVersion":1,' + "`n" + '"extra":true,'),
    $manifest.Replace('"role":"primary"}', '"role":"primary",}'),
    $manifest.Replace('"schemaVersion":1', '"schemaVersion":2'),
    $manifest.Replace('"schemaVersion":1', '"schemaVersion":"1"'),
    $manifest.Replace('"schemaVersion":1', '"schemaVersion":1,"schemaVersion":1'),
    $manifest.Replace('schemaVersion', 'SchemaVersion'),
    $manifest.Replace('"schemaVersion":1,', ''),
    $manifest.Replace('"packages":{', '"extra":true,"packages":{'),
    '{"schemaVersion":1,"packages":[]}', '{"schemaVersion":1,"packages":{}}',
    $manifest.Replace('KeelMatrix.KeyRingGuard', 'keelmatrix.keyringguard'),
    $manifest.Replace('KeelMatrix.KeyRingGuard', 'KeelMatrix.Other'),
    $manifest.Replace('"visibility":"public-product",', ''),
    $manifest.Replace('"role":"primary"', '"role":"primary","role":"primary"'),
    $manifest.Replace('"role":"primary"', '"role":false'),
    $manifest.Replace('"role":"primary"', '"role":"PRIMARY"'),
    $manifest.Replace('"role":"primary"', '"role":"unknown"'),
    $manifest.Replace('"role":"primary"', '"role":"component"'),
    $manifest.Replace('public-product', 'Public-Product'),
    $manifest.Replace('public-product', 'unknown'),
    $manifest.Replace('"public-product"', 'null'),
    $manifest.Replace('"role":"primary"', '"role":"primary","version":"0.1.0"'),
    '{"schemaVersion":1,"packages":{"KeelMatrix.KeyRingGuard":{"visibility":"public-product","role":"primary"},"KeelMatrix.KeyRingGuard":{"visibility":"public-product","role":"primary"}}}',
    $manifest.Replace('"KeelMatrix.KeyRingGuard":', '"KeelMatrix.KeyRingGuard":{},"KeelMatrix.KeyRingGuard":')
)) { Assert-Rejected { Assert-WebsiteMetadata $text @($source) } 'manifest shape/schema/ID/classification' }
foreach ($property in @('PackageId', 'PackageTags', 'PackageProjectUrl', 'RepositoryUrl', 'RepositoryType')) {
    $values = switch ($property) {
        'PackageId' { @('', 'keelmatrix.keyringguard', 'KeelMatrix.Other') }
        'PackageTags' { @(
            '', 'aspnetcore;keelmatrix-primary', 'aspnetcore;keelmatrix-public-product',
            ($source.PackageTags + ';keelmatrix-public-product'),
            ($source.PackageTags + ';keelmatrix-primary'),
            ($source.PackageTags + ';keelmatrix-internal-package'),
            ($source.PackageTags + ';keelmatrix-component'),
            $source.PackageTags.Replace('keelmatrix-public-product', 'KeelMatrix-Public-Product'),
            $source.PackageTags.Replace('keelmatrix-primary', 'KEELMATRIX-PRIMARY'),
            ($source.PackageTags + ';KEELMATRIX-PRIMARY'),
            $source.PackageTags.Replace('public-product', 'internal-package'),
            $source.PackageTags.Replace('primary', 'component')
        ) }
        'RepositoryType' { @('', 'Git', 'svn') }
        default { @(
            '', 'http://github.com/KeelMatrix/KeyRingGuard', 'https://keelmatrix.com',
            'https://github.com/Other/KeyRingGuard', 'https://github.com/KeelMatrix/Other',
            'https://github.com.evil.test/KeelMatrix/KeyRingGuard',
            'https://github.com/KeelMatrix/keyringguard',
            'https://github.com/KeelMatrix/KeyRingGuard?x=1',
            'https://github.com/KeelMatrix/KeyRingGuard/tree/main'
        ) }
    }
    foreach ($value in $values) {
        $changed = $source.PSObject.Copy()
        $changed.$property = $value
        Assert-Rejected { Assert-WebsiteMetadata $manifest @($changed) } "source $property"
        Assert-Rejected { Assert-WebsiteMetadata $manifest @($source) $changed } "packed $property"
    }
}
Assert-Rejected { Assert-WebsiteMetadata $manifest @() } 'no packable projects'
Assert-Rejected { Assert-WebsiteMetadata $manifest @($source, $source) } 'duplicate source ID'
$sibling = $source.PSObject.Copy()
$sibling.PackageId = 'KeelMatrix.KeyRingGuard.Component'
Assert-Rejected { Assert-WebsiteMetadata $manifest @($source, $sibling) } 'unclassified packable project'
$two = '{"schemaVersion":1,"packages":{"KeelMatrix.KeyRingGuard":{"visibility":"public-product","role":"primary"},"KeelMatrix.KeyRingGuard.Component":{"visibility":"public-product","role":"primary"}}}'
Assert-Rejected { Assert-WebsiteMetadata $two @($source, $sibling) } 'multiple primaries'
$component = $sibling.PSObject.Copy()
$component.PackageTags = $component.PackageTags.Replace('primary', 'component')
Assert-WebsiteMetadata ($two.Replace('Component":{"visibility":"public-product","role":"primary"', 'Component":{"visibility":"public-product","role":"component"')) @($source, $component)
$zero = $source.PSObject.Copy()
$zero.PackageTags = $zero.PackageTags.Replace('primary', 'component')
Assert-Rejected { Assert-WebsiteMetadata ($manifest.Replace('primary', 'component')) @($zero) } 'zero primaries'
$internal = $source.PSObject.Copy()
$internal.PackageTags = $internal.PackageTags.Replace('public-product', 'internal-package')
Assert-Rejected { Assert-WebsiteMetadata ($manifest.Replace('public-product', 'internal-package')) @($internal) } 'hidden public product'
Assert-Rejected { Assert-WebsiteMetadata ($two.Replace('KeelMatrix.KeyRingGuard.Component', 'keelmatrix.keyringguard')) @($source, $sibling) } 'case-folded duplicate ID'
$packed = $source.PSObject.Copy()
$packed.PackageTags = $packed.PackageTags.Replace(';testing', '')
Assert-Rejected { Assert-WebsiteMetadata $manifest @($source) $packed } 'lost marketing tag'
$packed.PackageTags = $source.PackageTags.Replace(';', ' ')
Assert-WebsiteMetadata $manifest @($source) $packed

# Machine identity must distinguish strings that linguistic comparison can equate.
foreach ($codePoint in @(0x200B, 0x00AD, 0x2060)) {
    $character = [string][char]$codePoint
    $label = 'U+{0:X4}' -f $codePoint
    foreach ($pair in @(@('visibility', 'public-product'), @('role', 'primary'))) {
        $value = $pair[1].Insert(2, $character)
        Assert-Rejected { Assert-WebsiteMetadata ($manifest.Replace($pair[1], $value)) @($source) } "manifest $($pair[0]) $label"
    }
    foreach ($propertyName in @('schemaVersion', 'packages', 'visibility', 'role')) {
        Assert-Rejected { Assert-WebsiteMetadata ($manifest.Replace($propertyName, $propertyName.Insert(2, $character))) @($source) } "manifest property $propertyName $label"
    }
    foreach ($property in @('PackageId', 'PackageProjectUrl', 'RepositoryUrl', 'RepositoryType')) {
        $changed = $source.PSObject.Copy()
        $changed.$property = $changed.$property.Insert(2, $character)
        Assert-Rejected { Assert-WebsiteMetadata $manifest @($changed) } "source $property $label"
        Assert-Rejected { Assert-WebsiteMetadata $manifest @($source) $changed } "packed $property $label"
    }
    Assert-Rejected { Assert-WebsiteMetadata ($manifest.Replace('KeyRingGuard', "KeyRing${character}Guard")) @($source) } "manifest ID $label"
    foreach ($sentinel in @('keelmatrix-public-product', 'keelmatrix-primary')) {
        $changed = $source.PSObject.Copy()
        $changed.PackageTags = $changed.PackageTags.Replace($sentinel, ($sentinel + $character))
        Assert-Rejected { Assert-WebsiteMetadata $manifest @($changed) } "source sentinel $label"
        Assert-Rejected { Assert-WebsiteMetadata $manifest @($source) $changed } "packed sentinel $label"
    }
    $changed = $source.PSObject.Copy()
    $changed.PackageTags = $changed.PackageTags.Replace('aspnetcore', "aspnet${character}core")
    Assert-Rejected { Assert-WebsiteMetadata $manifest @($source) $changed } "packed marketing tag $label"
    # Exact non-ASCII values are legal marketing tags; their order and separators are not identity.
    $same = $source.PSObject.Copy()
    $same.PackageTags += ";aspnet${character}core;aspnetcore"
    $reordered = $same.PSObject.Copy()
    $tags = [string[]]@(Get-WebsiteTags $same.PackageTags)
    [array]::Reverse($tags)
    $reordered.PackageTags = $tags -join ", `t"
    Assert-WebsiteMetadata $manifest @($same) $reordered
    $reordered.PackageTags = $reordered.PackageTags.Replace("aspnet${character}core", 'aspnetcore')
    Assert-Rejected { Assert-WebsiteMetadata $manifest @($same) $reordered } "ordinal tag multiset $label"
}

# Exercise real inherited MSBuild properties, archive parsing and child failure propagation.
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$scratch = Join-Path $tempRoot ('keyringguard-website-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $scratch 'src/KeelMatrix.KeyRingGuard') -Force | Out-Null
try {
    [IO.File]::WriteAllText((Join-Path $scratch 'global.json'), [IO.File]::ReadAllText((Join-Path $root 'global.json')))
    $manifestPath = Join-Path $scratch 'keelmatrix.website.json'
    [IO.File]::WriteAllText($manifestPath, $manifest)
    $props = '<Project><PropertyGroup><IsPackable>true</IsPackable><PackageId>KeelMatrix.KeyRingGuard</PackageId><PackageTags>' + $source.PackageTags + '</PackageTags><PackageProjectUrl>' + $source.PackageProjectUrl + '</PackageProjectUrl><RepositoryUrl>' + $source.RepositoryUrl + '</RepositoryUrl><RepositoryType>git</RepositoryType></PropertyGroup></Project>'
    $propsPath = Join-Path $scratch 'Directory.Build.props'
    [IO.File]::WriteAllText($propsPath, $props)
    [IO.File]::WriteAllText((Join-Path $scratch 'src/KeelMatrix.KeyRingGuard/KeelMatrix.KeyRingGuard.csproj'), '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>')
    $validator = Join-Path $root 'scripts/Test-WebsiteMetadata.ps1'
    & $validator -RepositoryRoot $scratch
    foreach ($codePoint in @(0x200B, 0x00AD, 0x2060)) {
        foreach ($property in @('PackageId', 'PackageProjectUrl', 'RepositoryUrl', 'RepositoryType')) {
            $changed = $source.$property.Insert(2, [string][char]$codePoint)
            [IO.File]::WriteAllText($propsPath, $props.Replace("<$property>$($source.$property)</$property>", "<$property>$changed</$property>"))
            Assert-Rejected { & $validator -RepositoryRoot $scratch } "evaluated source $property U+$('{0:X4}' -f $codePoint)"
        }
    }
    [IO.File]::WriteAllText($propsPath, $props)
    New-Item -ItemType Directory -Path (Join-Path $scratch 'tests/Consumer') | Out-Null
    $consumerPath = Join-Path $scratch 'tests/Consumer/Consumer.csproj'
    $consumer = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><IsPackable>false</IsPackable><PackageId>KeelMatrix.KeyRingGuard.Consumer</PackageId></PropertyGroup></Project>'
    [IO.File]::WriteAllText($consumerPath, $consumer)
    & $validator -RepositoryRoot $scratch
    [IO.File]::WriteAllText($consumerPath, $consumer.Replace('<IsPackable>false', '<IsPackable>true'))
    Assert-Rejected { & $validator -RepositoryRoot $scratch } 'unclassified packable project outside src'
    [IO.File]::WriteAllText($consumerPath, $consumer)
    $packagePath = Join-Path $scratch 'fixture.nupkg'
    function Write-Package([string]$Tags, [string]$Extra = '', [string]$TargetPath = $packagePath) {
        if (Test-Path -LiteralPath $TargetPath) { Remove-Item -LiteralPath $TargetPath }
        $zip = [IO.Compression.ZipFile]::Open($TargetPath, [IO.Compression.ZipArchiveMode]::Create)
        try {
            $entry = $zip.CreateEntry('KeelMatrix.KeyRingGuard.nuspec')
            $writer = [IO.StreamWriter]::new($entry.Open())
            try { $writer.Write('<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"><metadata><id>KeelMatrix.KeyRingGuard</id><tags>' + $Tags + '</tags><projectUrl>' + $source.PackageProjectUrl + '</projectUrl><repository type="git" url="' + $source.RepositoryUrl + '" />' + $Extra + '</metadata></package>') }
            finally { $writer.Dispose() }
        }
        finally { $zip.Dispose() }
    }
    Write-Package $source.PackageTags.Replace(';', ' ')
    & $validator -RepositoryRoot $scratch -PackagePath $packagePath
    Write-Package $source.PackageTags '' ([IO.Path]::ChangeExtension($packagePath, '.snupkg'))
    Write-Package 'aspnetcore testing'
    Assert-Rejected { & $validator -RepositoryRoot $scratch -PackagePath $packagePath } 'archive missing sentinels'
    $inspectionRejected = $false
    try { & (Join-Path $root 'scripts/Inspect-Package.ps1') -PackagePath $packagePath -AllowMissingIcon }
    catch {
        if ($_.Exception.Message -notmatch '^Website classification tags') { throw }
        $inspectionRejected = $true
    }
    if (-not $inspectionRejected) { throw 'Package inspection bypassed website metadata validation.' }
    Write-Package $source.PackageTags '<tags>keelmatrix-primary</tags>'
    Assert-Rejected { & $validator -RepositoryRoot $scratch -PackagePath $packagePath } 'duplicate nuspec tags'
    [IO.File]::WriteAllText((Join-Path $scratch 'Directory.Build.targets'), '<Project><PropertyGroup><PackageTags>keelmatrix-public-product</PackageTags></PropertyGroup></Project>')
    Assert-Rejected { & $validator -RepositoryRoot $scratch } 'inherited Release override'
    [IO.File]::WriteAllText((Join-Path $scratch 'Directory.Build.targets'), '<Project />')
    foreach ($valid in @($false, $true)) {
        [IO.File]::WriteAllText($manifestPath, $(if ($valid) { $manifest } else { '{}' }))
        $argumentsPath = Join-Path $scratch 'arguments.json'
        [IO.File]::WriteAllText($argumentsPath, (ConvertTo-Json -InputObject @('-RepositoryRoot', $scratch)))
        $marker = Join-Path $scratch 'following-operation'
        $null = @(Invoke-NestedPwsh -NoProfile -File (Join-Path $PSScriptRoot 'Invoke-GateFailureProbe.ps1') -CommandFile $validator -ArgumentsPath $argumentsPath -MarkerPath $marker 2>&1)
        if (($LASTEXITCODE -eq 0) -ne $valid -or (Test-Path -LiteralPath $marker) -ne $valid) { throw 'Website metadata validation did not control the following operation.' }
    }
    Remove-Item -LiteralPath $manifestPath
    Assert-Rejected { & $validator -RepositoryRoot $scratch } 'missing manifest file'
}
finally {
    if (-not ([IO.Path]::GetFullPath($scratch)).StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe website fixture cleanup path.' }
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
Write-Output "Website metadata contract: PASS ($count negative controls; inherited source, archive and child failure boundaries)."
& (Join-Path $PSScriptRoot 'VerifyWebsiteProjectSelection.ps1')
exit 0
