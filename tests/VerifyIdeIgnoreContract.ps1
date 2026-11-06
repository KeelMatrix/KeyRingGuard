[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../build/Invoke-NestedPwsh.ps1')
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$validator = Join-Path $root 'scripts/Test-IdeIgnoreContract.ps1'
$rules = @(Get-Content -LiteralPath (Join-Path $root '.gitignore'))
$cases = @(& (Join-Path $root 'scripts/Get-IdeIgnoreCases.ps1'))
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$scratch = Join-Path $tempRoot ('keyringguard-ignore-negatives-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
try {
    $output = @(Invoke-NestedPwsh -NoProfile -File $validator 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "Ignore positive control failed: $output" }
    foreach ($case in $cases) {
        foreach ($replacement in @('', '__not_a_noise_pattern__')) {
            if ($rules -cnotcontains $case.Pattern) { throw "Negative fixture cannot locate pattern $($case.Pattern)." }
            $mutant = $rules | ForEach-Object { if ($_ -ceq $case.Pattern) { $replacement } else { $_ } }
            $path = Join-Path $scratch 'mutated-ignore'
            [IO.File]::WriteAllLines($path, [string[]]$mutant)
            $null = @(Invoke-NestedPwsh -NoProfile -File $validator -IgnorePath $path 2>&1)
            if ($LASTEXITCODE -eq 0) { throw "Ignore mutation passed for class $($case.Pattern)." }
        }
    }
    foreach ($mutant in @(
        ($rules -creplace '^\.vs/$', '/.vs/'),
        ($rules + '!.DS_Store'),
        (@('!.env.example') + @($rules | Where-Object { $_ -cne '!.env.example' -and $_ -cne '!**/.env.example' }))
    )) {
        $path = Join-Path $scratch 'mutated-ignore'
        [IO.File]::WriteAllLines($path, [string[]]$mutant)
        $null = @(Invoke-NestedPwsh -NoProfile -File $validator -IgnorePath $path 2>&1)
        if ($LASTEXITCODE -eq 0) { throw 'Anchoring, negation, or exception-order mutation passed.' }
    }
    $externalExcludes = Join-Path $scratch 'external-excludes'
    [IO.File]::WriteAllText($externalExcludes, '*')
    $externalConfig = Join-Path $scratch 'external.config'
    [IO.File]::WriteAllText($externalConfig, "[core]`nexcludesFile = " + $externalExcludes.Replace([char]92, '/') + "`n")
    $previousGlobal = $env:GIT_CONFIG_GLOBAL
    $previousSystem = $env:GIT_CONFIG_SYSTEM
    try {
        $env:GIT_CONFIG_GLOBAL = $externalConfig
        $env:GIT_CONFIG_SYSTEM = $externalConfig
        $path = Join-Path $scratch 'mutated-ignore'
        [IO.File]::WriteAllLines($path, [string[]]@($rules | Where-Object { $_ -cne '*.userprefs' }))
        $null = @(Invoke-NestedPwsh -NoProfile -File $validator -IgnorePath $path 2>&1)
        if ($LASTEXITCODE -eq 0) { throw 'External global/system excludes hid a missing repository rule.' }
    }
    finally {
        if ($null -eq $previousGlobal) { Remove-Item Env:GIT_CONFIG_GLOBAL -ErrorAction SilentlyContinue } else { $env:GIT_CONFIG_GLOBAL = $previousGlobal }
        if ($null -eq $previousSystem) { Remove-Item Env:GIT_CONFIG_SYSTEM -ErrorAction SilentlyContinue } else { $env:GIT_CONFIG_SYSTEM = $previousSystem }
    }
    Write-Output "IDE ignore negatives: PASS ($($cases.Count * 2 + 4) removal/mutation/global-exclude cases)."
}
finally {
    if (-not ([IO.Path]::GetFullPath($scratch)).StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture cleanup path.' }
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
exit 0
