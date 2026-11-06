[CmdletBinding()]
param(
    [string]$PackagePath = (Join-Path $PSScriptRoot '..' 'artifacts' 'packages' 'KeelMatrix.KeyRingGuard.0.1.0.nupkg')
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Invoke-BoundedDotnet.ps1')
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ('keyringguard-package-smoke-' + [Guid]::NewGuid().ToString('N'))
$feed = Join-Path $scratch 'feed'
$packages = Join-Path $scratch 'packages'
$nugetConfig = Join-Path $scratch 'NuGet.config'
New-Item -ItemType Directory -Force -Path $feed | Out-Null
New-Item -ItemType Directory -Force -Path $packages | Out-Null
Copy-Item -LiteralPath $package -Destination $feed
$escapedFeed = [Security.SecurityElement]::Escape($feed)
Set-Content -LiteralPath $nugetConfig -Value @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$escapedFeed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local">
      <package pattern="KeelMatrix.KeyRingGuard" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
"@ -NoNewline

$previousNuGetPackages = $env:NUGET_PACKAGES
$env:NUGET_PACKAGES = $packages
try {
    $consumer = Join-Path $root 'tests' 'KeelMatrix.KeyRingGuard.PackageConsumer' 'KeelMatrix.KeyRingGuard.PackageConsumer.csproj'
    $consumerSourcePath = Join-Path $root 'tests' 'KeelMatrix.KeyRingGuard.PackageConsumer' 'Program.cs'
    $lineBreak = '(?:' + [char]13 + ')?' + [char]10
    $quickStartPattern = '(?ms)^## Quick Start' + $lineBreak + '.*?^```csharp' + $lineBreak + '(?<snippet>.*?)^```'
    $quickStartSources = @(
        (Join-Path $root 'README.md')
        (Join-Path $root 'docs' 'usage.md')
        (Join-Path $root 'src' 'KeelMatrix.KeyRingGuard' 'README.md')
    )
    $documentedSnippets = foreach ($quickStartSource in $quickStartSources) {
        $document = Get-Content -Raw -LiteralPath $quickStartSource -Encoding utf8
        $quickStart = [regex]::Match($document, $quickStartPattern)
        if (-not $quickStart.Success) { throw "Could not locate the documented Quick Start C# snippet in $quickStartSource." }
        [pscustomobject]@{
            Path = $quickStartSource
            Snippet = ($quickStart.Groups['snippet'].Value -replace "`r`n", "`n").TrimEnd([char]10)
        }
    }
    $documentedSnippet = $documentedSnippets[0].Snippet
    foreach ($documented in $documentedSnippets) {
        if (-not [string]::Equals($documented.Snippet, $documentedSnippet, [StringComparison]::Ordinal)) {
            throw "The Quick Start snippet in $($documented.Path) must match the other shipped Quick Start snippets verbatim."
        }
    }
    $consumerSource = (Get-Content -Raw -LiteralPath $consumerSourcePath -ErrorAction Stop -Encoding utf8) -replace "`r`n", "`n"
    $consumerSource = $consumerSource.TrimEnd([char]10)
    if (-not [string]::Equals($consumerSource, $documentedSnippet, [StringComparison]::Ordinal)) {
        throw 'The package consumer source must match the documented Quick Start snippet verbatim.'
    }

    dotnet restore $consumer --configfile $nugetConfig --no-cache --force --packages $packages
    if ($LASTEXITCODE -ne 0) { throw "Package consumer restore failed with exit code $LASTEXITCODE." }
    dotnet build $consumer --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Package consumer build failed with exit code $LASTEXITCODE." }
    dotnet run --project $consumer --configuration Release --no-restore --no-build
    if ($LASTEXITCODE -ne 0) { throw "Package consumer smoke failed with exit code $LASTEXITCODE." }

    # Compile outside the repository so no repository props, project references, or
    # dependency overrides can hide a problem in the actual PackageReference consumer.
    $lifecycle = Join-Path $scratch 'lifecycle'
    New-Item -ItemType Directory -Path $lifecycle | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'tests' 'PackageLifecycleProbe.cs') -Destination (Join-Path $lifecycle 'Program.cs')
    Copy-Item -LiteralPath (Join-Path $root 'tests' 'CancellationLifetimeProbe.cs') -Destination (Join-Path $lifecycle 'CancellationLifetimeProbe.cs')
    $lifecycleProject = Join-Path $lifecycle 'Lifecycle.csproj'
    Set-Content -LiteralPath $lifecycleProject -Value @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework><OutputType>Exe</OutputType>
    <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="KeelMatrix.KeyRingGuard" Version="0.1.0" />
    <PackageReference Include="Microsoft.AspNetCore.DataProtection.Extensions" Version="8.0.31" />
  </ItemGroup>
</Project>
'@
    dotnet restore $lifecycleProject --configfile $nugetConfig --no-cache --force --packages $packages
    if ($LASTEXITCODE -ne 0) { throw "Lifecycle consumer restore failed with exit code $LASTEXITCODE." }
    dotnet build $lifecycleProject --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Lifecycle consumer build failed with exit code $LASTEXITCODE." }
    $lifecycleDll = Join-Path $lifecycle 'bin' 'Release' 'net8.0' 'Lifecycle.dll'
    Invoke-BoundedDotnet -Arguments @($lifecycleDll)
    Invoke-BoundedDotnet -Arguments @($lifecycleDll, '--throwing-handlers')
    # Real nonzero-exit and deadline controls prove failures reach this smoke gate.
    $exitRejected = $false
    try { Invoke-BoundedDotnet -Arguments @($lifecycleDll, '--exit-probe') }
    catch { if ($_.Exception.Data['ExitCode'] -ne 23) { throw }; $exitRejected = $true }
    if (-not $exitRejected) { throw 'The child-process runner accepted a failing package probe.' }
    $timeoutRejected = $false
    try { Invoke-BoundedDotnet -Arguments @($lifecycleDll, '--wait-probe') -TimeoutMilliseconds 250 }
    catch { if (-not $_.Exception.Data['TimedOut']) { throw }; $timeoutRejected = $true }
    if (-not $timeoutRejected) { throw 'The child-process runner accepted an over-deadline package probe.' }
    Write-Output 'Package child-process failure propagation: PASS (exit 23 and bounded termination)'

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($package)
    try {
        $entryStream = $archive.GetEntry('lib/net8.0/KeelMatrix.KeyRingGuard.dll').Open()
        try { $packedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($entryStream)) }
        finally { $entryStream.Dispose() }
    }
    finally { $archive.Dispose() }
    $loadedHash = (Get-FileHash -LiteralPath (Join-Path $lifecycle 'bin' 'Release' 'net8.0' 'KeelMatrix.KeyRingGuard.dll') -Algorithm SHA256).Hash
    if ($loadedHash -ne $packedHash) { throw 'The loaded consumer assembly differs from the package DLL.' }
    Write-Output "PACKAGE_DLL_SHA256=$packedHash LOADED_DLL_SHA256=$loadedHash"
    Write-Output "NUPKG_SHA256=$((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash)"
    Write-Output 'Package consumer smoke: PASS'
} finally {
    if ($null -eq $previousNuGetPackages) { Remove-Item Env:NUGET_PACKAGES -ErrorAction SilentlyContinue } else { $env:NUGET_PACKAGES = $previousNuGetPackages }
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
