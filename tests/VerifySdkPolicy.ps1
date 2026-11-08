[CmdletBinding()]
param([Parameter(Mandatory)][string]$HostFxr)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$policyText = [IO.File]::ReadAllText((Join-Path $root 'global.json'))
. (Join-Path $root 'build/SdkSelection.ps1')
$policy = ($policyText | ConvertFrom-Json).sdk
$baseline = [version](($policyText | ConvertFrom-Json).sdk.version)
$band = [int][math]::Floor($baseline.Build / 100) * 100
$prefix = "$($baseline.Major).$($baseline.Minor)."
$exact = $baseline.ToString()
$later = $prefix + ($baseline.Build + 1)
$last = $prefix + ($band + 99)
$preview = $later + '-preview.1'
$cases = @(
    @{ Name = 'absent'; Installed = @(); Expected = '' },
    @{ Name = 'exact'; Installed = @($exact); Expected = $exact },
    @{ Name = 'exact-preferred'; Installed = @($exact, $later, $last); Expected = $exact },
    @{ Name = 'servicing-only'; Installed = @($later); Expected = $later },
    @{ Name = 'latest-servicing-fallback'; Installed = @($later, $last); Expected = $last },
    @{ Name = 'below-minimum'; Installed = @($prefix + ($baseline.Build - 1)); Expected = '' },
    @{ Name = 'prior-band'; Installed = @($prefix + ($band - 1)); Expected = '' },
    @{ Name = 'next-band'; Installed = @($prefix + ($band + 100)); Expected = '' },
    @{ Name = 'next-minor'; Installed = @("$($baseline.Major).$($baseline.Minor + 1).$($baseline.Build)"); Expected = '' },
    @{ Name = 'next-major'; Installed = @("$($baseline.Major + 1).$($baseline.Minor).$($baseline.Build)"); Expected = '' },
    @{ Name = 'preview-only'; Installed = @($preview); Expected = '' },
    @{ Name = 'exact-preview'; Installed = @($exact + '-preview.1'); Expected = '' },
    @{ Name = 'stable-and-preview'; Installed = @($later, $preview); Expected = $later }
)
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$scratch = Join-Path $tempRoot ('keyringguard-sdk-policy-' + [guid]::NewGuid().ToString('N'))
try {
    Add-Type -Path (Join-Path $PSScriptRoot 'SdkResolverProbe.cs')
    foreach ($case in $cases) {
        $fixture = Join-Path $scratch $case.Name
        $project = Join-Path $fixture 'solution/project'
        $hostRoot = Join-Path $fixture 'host'
        New-Item -ItemType Directory -Path $project, $hostRoot -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $fixture 'solution/global.json'), $policyText)
        foreach ($version in $case.Installed) {
            $sdk = Join-Path $hostRoot "sdk/$version"
            New-Item -ItemType Directory -Path $sdk -Force | Out-Null
            [IO.File]::WriteAllText((Join-Path $sdk 'dotnet.dll'), '')
        }
        # Both preview-enabled and stable IDE/CLI requests must obey explicit false.
        foreach ($disallowPreview in @($false, $true)) {
            $selected = [KeyRingGuard.SdkFixture.SdkResolverProbe]::Select($HostFxr, $hostRoot, $project, $disallowPreview)
            $actual = if ($selected) { [IO.Path]::GetFileName($selected) } else { '' }
            if ($actual -cne $case.Expected) { throw "SDK case $($case.Name), disallowPreview=$disallowPreview expected '$($case.Expected)', got '$actual'." }
        }
    }
    foreach ($selected in @($exact, $later, $last, ($prefix + ($baseline.Build - 1)), ($prefix + ($band + 100)), $preview, '')) {
        foreach ($strict in @($false, $true)) {
            $expected = $selected -ceq $exact -or (-not $strict -and $selected -cin @($later, $last))
            $accepted = $true
            try { Assert-SdkSelection $policy $selected -RequireExact:$strict } catch { $accepted = $false }
            if ($accepted -ne $expected) { throw "SDK script policy mismatch: selected='$selected', strict=$strict." }
        }
    }
    foreach ($mutation in @('latestPatch', 'latestFeature', 'latestMajor')) {
        $invalid = $policyText | ConvertFrom-Json
        $invalid.sdk.rollForward = $mutation
        $rejected = $false
        try { Assert-SdkSelection $invalid.sdk $exact } catch { $rejected = $true }
        if (-not $rejected) { throw 'Unsupported roll-forward policy passed validation.' }
    }
    foreach ($previewSetting in @($true, 'false', $null)) {
        $invalid = $policyText | ConvertFrom-Json
        $invalid.sdk.allowPrerelease = $previewSetting
        $rejected = $false
        try { Assert-SdkSelection $invalid.sdk $exact } catch { $rejected = $true }
        if (-not $rejected) { throw 'A missing, string or true prerelease policy passed validation.' }
    }
    Write-Output "SDK policy: PASS ($($cases.Count * 2) native resolver cases; exact-first, servicing boundaries, preview settings and nested search)."
    Write-Output 'SDK script policy: PASS (14 local/strict selection cases and six invalid-policy controls).'
}
finally {
    if (-not ([IO.Path]::GetFullPath($scratch)).StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture cleanup path.' }
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
