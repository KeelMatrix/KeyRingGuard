function New-ChangelogFixture([string]$Root, [string]$Version = '0.1.0') {
    New-Item -ItemType Directory -Force -Path (Join-Path $Root 'src/KeelMatrix.KeyRingGuard') | Out-Null
    # Finalized-release fixtures still require an exact SDK, including on local
    # servicing installations. Validate that SDK against the repository policy.
    . (Join-Path $PSScriptRoot '../build/SdkSelection.ps1')
    Push-Location (Join-Path $PSScriptRoot '..')
    try {
        $sdk = (Get-Content -Raw -LiteralPath 'global.json' | ConvertFrom-Json).sdk
        $selected = (& dotnet --version | Out-String).Trim()
        if ($LASTEXITCODE -ne 0) { throw 'The changelog fixture requires a working SDK.' }
        Assert-SdkSelection $sdk $selected
    }
    finally { Pop-Location }
    $fixturePolicy = @{ sdk = @{ version = $selected; rollForward = 'disable'; allowPrerelease = $false } } | ConvertTo-Json
    [IO.File]::WriteAllText((Join-Path $Root 'global.json'), $fixturePolicy)
    [IO.File]::WriteAllText((Join-Path $Root 'Directory.Build.props'), '<Project><PropertyGroup><Version>' + $Version + '</Version><PackageVersion>$(Version)</PackageVersion></PropertyGroup></Project>')
    [IO.File]::WriteAllText((Join-Path $Root 'Directory.Packages.props'), '<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup><ItemGroup><PackageVersion Include="KeelMatrix.KeyRingGuard" Version="' + $Version + '" /></ItemGroup></Project>')
    [IO.File]::WriteAllText((Join-Path $Root 'src/KeelMatrix.KeyRingGuard/KeelMatrix.KeyRingGuard.csproj'), '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework><PackageId>KeelMatrix.KeyRingGuard</PackageId></PropertyGroup></Project>')
    [IO.File]::WriteAllText((Join-Path $Root 'CHANGELOG.md'), "# Changelog`n`n## [Unreleased]`n`n## [$Version] - 2026-09-26`n`n### Added`n`n- Provides continuity verification.")
}
