[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../build/Invoke-NestedPwsh.ps1')
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$validator = Join-Path $root 'scripts/Test-WebsiteMetadata.ps1'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$scratch = Join-Path $tempRoot ('keyringguard-project-selection-' + [guid]::NewGuid().ToString('N'))
$count = 0
function Write-FixtureFile([string]$RelativePath, [string]$Text) {
    $path = Join-Path $scratch $RelativePath
    $null = New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($path))
    [IO.File]::WriteAllText($path, $Text)
    return $path
}
function Assert-Selection([bool]$Valid, [string]$Name) {
    $rejected = $false
    try { $null = & $validator -RepositoryRoot $scratch }
    catch { $rejected = $true }
    if ($rejected -eq $Valid) { throw "Incorrect project selection for $Name (expected valid: $Valid)." }
    $script:count++
}
try {
    $null = Write-FixtureFile 'global.json' ([IO.File]::ReadAllText((Join-Path $root 'global.json')))
    $manifest = [IO.File]::ReadAllText((Join-Path $root 'keelmatrix.website.json'))
    $null = Write-FixtureFile 'keelmatrix.website.json' $manifest
    $props = '<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework><IsPackable>false</IsPackable><PackageId>KeelMatrix.KeyRingGuard</PackageId><PackageTags>testing;keelmatrix-public-product;keelmatrix-primary</PackageTags><PackageProjectUrl>https://github.com/KeelMatrix/KeyRingGuard#readme</PackageProjectUrl><RepositoryUrl>https://github.com/KeelMatrix/KeyRingGuard</RepositoryUrl><RepositoryType>git</RepositoryType></PropertyGroup></Project>'
    $null = Write-FixtureFile 'Directory.Build.props' $props
    $main = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><IsPackable>True</IsPackable></PropertyGroup></Project>'
    $mainPath = Write-FixtureFile 'src/KeelMatrix.KeyRingGuard/KeelMatrix.KeyRingGuard.csproj' $main
    Assert-Selection $true 'inherited metadata on the intended shipping project'
    foreach ($extension in @('vbproj', 'fsproj', 'csproj')) {
        foreach ($location in @('.', 'src', 'tests', 'smoke', 'examples', 'tools', '.build')) {
            $helperPath = Write-FixtureFile "$location/Helper/Helper.$extension" '<Project Sdk="Microsoft.NET.Sdk" />'
            Assert-Selection $true "$location/$extension inherited nonpackable"
            [IO.File]::WriteAllText($helperPath, '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><IsPackable>True</IsPackable><PackageId>KeelMatrix.Unclassified</PackageId><PackageTags>testing</PackageTags></PropertyGroup></Project>')
            Assert-Selection $false "$location/$extension unclassified packable"
            Remove-Item -LiteralPath $helperPath
        }
        $helperPath = Write-FixtureFile "tools/Helper/Helper.$extension" '<Project Sdk="Microsoft.NET.Sdk" />'
        foreach ($case in @(
            @{ Name = 'lowercase'; Value = 'true'; Valid = $false },
            @{ Name = 'uppercase'; Value = 'TRUE'; Valid = $false },
            @{ Name = 'disabled'; Value = 'FALSE'; Valid = $true },
            @{ Name = 'Release enabled'; Value = 'false'; Override = '<IsPackable Condition="''$(Configuration)'' == ''Release''">True</IsPackable>'; Valid = $false },
            @{ Name = 'Release disabled'; Value = 'true'; Override = '<IsPackable Condition="''$(Configuration)'' == ''Release''">false</IsPackable>'; Valid = $true },
            @{ Name = 'targets enabled'; Value = 'false'; Targets = '<IsPackable>True</IsPackable>'; Valid = $false },
            @{ Name = 'targets disabled'; Value = 'true'; Targets = '<IsPackable>FALSE</IsPackable>'; Valid = $true }
        )) {
            [IO.File]::WriteAllText($helperPath, ('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><IsPackable>' + $case.Value + '</IsPackable>' + $case.Override + '</PropertyGroup></Project>'))
            $null = Write-FixtureFile 'Directory.Build.targets' ('<Project><PropertyGroup Condition="''$(MSBuildProjectName)'' == ''Helper''">' + $case.Targets + '</PropertyGroup></Project>')
            Assert-Selection $case.Valid "$extension $($case.Name), inherited shipping ID and tags"
        }
        $null = Write-FixtureFile 'Directory.Build.targets' '<Project />'
        # Nearer imports and project overrides use the real SDK's final Release properties.
        $nearProps = Write-FixtureFile 'tools/Helper/Directory.Build.props' $props.Replace('<IsPackable>false', '<IsPackable>True')
        [IO.File]::WriteAllText($helperPath, '<Project Sdk="Microsoft.NET.Sdk" />')
        Assert-Selection $false "$extension nearer props enable packing"
        [IO.File]::WriteAllText($helperPath, '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><IsPackable>false</IsPackable></PropertyGroup></Project>')
        Assert-Selection $true "$extension project disables inherited packing"
        Remove-Item -LiteralPath $nearProps
        [IO.File]::WriteAllText($helperPath, '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><IsPackable>True</IsPackable></PropertyGroup></Project>')
        # Giving an extra project a manifest entry cannot expand this single-package repository.
        [IO.File]::WriteAllText($helperPath, '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><IsPackable>True</IsPackable><PackageId>KeelMatrix.Helper</PackageId><PackageTags>testing;keelmatrix-public-product;keelmatrix-component</PackageTags></PropertyGroup></Project>')
        $null = Write-FixtureFile 'keelmatrix.website.json' '{"schemaVersion":1,"packages":{"KeelMatrix.KeyRingGuard":{"visibility":"public-product","role":"primary"},"KeelMatrix.Helper":{"visibility":"public-product","role":"component"}}}'
        Assert-Selection $false "$extension classified extra shipping project"
        $null = Write-FixtureFile 'keelmatrix.website.json' $manifest
        $argumentsPath = Write-FixtureFile 'arguments.json' (ConvertTo-Json -InputObject @('-RepositoryRoot', $scratch))
        $marker = Join-Path $scratch "following-$extension"
        $null = @(Invoke-NestedPwsh -NoProfile -File (Join-Path $PSScriptRoot 'Invoke-GateFailureProbe.ps1') -CommandFile $validator -ArgumentsPath $argumentsPath -MarkerPath $marker 2>&1)
        if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $marker)) { throw "Native $extension failure advanced to a following operation." }
        Remove-Item -LiteralPath $helperPath
        # Case-insensitive extension discovery also applies on case-sensitive hosts.
        $upper = Write-FixtureFile "tools/Upper/Upper.$($extension.ToUpperInvariant())" '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><IsPackable>True</IsPackable></PropertyGroup></Project>'
        Assert-Selection $false "$extension uppercase extension"
        Remove-Item -LiteralPath $upper
        foreach ($generated in @('bin', 'obj', 'artifacts', '.git', 'tests/bin', 'tools/OBJ')) {
            $null = Write-FixtureFile "$generated/Generated.$extension" 'not an MSBuild project'
        }
    }
    Assert-Selection $true 'generated directories excluded for every language'
    [IO.File]::WriteAllText($mainPath, $main.Replace('True', 'FALSE'))
    Assert-Selection $false 'shipping project disabled in Release'
    [IO.File]::WriteAllText($mainPath, $main)
    $null = Write-FixtureFile 'Directory.Build.targets' '<Project><PropertyGroup><PackageTags>testing</PackageTags></PropertyGroup></Project>'
    Assert-Selection $false 'inherited targets remove shipping classification'
    Write-Output "Website project selection: PASS ($count real SDK controls; C#/VB/F#, locations, inheritance, Release, casing, single shipping boundary and native failures)."
}
finally {
    if (-not ([IO.Path]::GetFullPath($scratch)).StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe project fixture cleanup path.' }
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
