[CmdletBinding()]
param(
    [string]$PackagePath = (Join-Path $PSScriptRoot '..' 'artifacts' 'packages' 'KeelMatrix.KeyRingGuard.0.1.0.nupkg')
)

$ErrorActionPreference = 'Stop'
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
    Write-Output 'Package consumer smoke: PASS'
} finally {
    if ($null -eq $previousNuGetPackages) { Remove-Item Env:NUGET_PACKAGES -ErrorAction SilentlyContinue } else { $env:NUGET_PACKAGES = $previousNuGetPackages }
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
