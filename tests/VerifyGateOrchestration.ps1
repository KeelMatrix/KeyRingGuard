[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../build/Invoke-NestedPwsh.ps1')
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$local = [IO.File]::ReadAllText((Join-Path $root 'scripts/Invoke-LocalGate.ps1'))
$ci = [IO.File]::ReadAllText((Join-Path $root '.github/workflows/ci.yml'))
$required = @('VerifyIdeIgnoreContract.ps1', 'VerifyPublicationContract.ps1', 'VerifyChangelogContract.ps1', 'VerifySdkIsolation.ps1')
function Assert-Wiring([string]$LocalText, [string]$CiText) {
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseInput($LocalText, [ref]$tokens, [ref]$errors)
    if ($errors.Count -ne 0) { throw 'Invalid local gate syntax.' }
    $strings = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.StringConstantExpressionAst] }, $true) | ForEach-Object Value)
    if ($strings -cnotcontains 'Invoke-GateStep.ps1') { throw 'Local gate must use the checked step runner.' }
    foreach ($name in $required) {
        if ($strings -cnotcontains $name) { throw "Local gate omits $name." }
        $step = [regex]::Match($CiText, '(?ms)^      - name:[^\r\n]*\r?\n(?:(?!^      - name:).)*run: pwsh -NoProfile -File tests/' + [regex]::Escape($name) + '(?:(?!^      - name:).)*')
        if (-not $step.Success -or $step.Value -match '(?m)^        (?:if|continue-on-error):') { throw "CI must run $name unconditionally and propagate failure." }
    }
}
Assert-Wiring $local $ci
foreach ($name in $required) {
    foreach ($scope in @('local', 'ci')) {
        $rejected = $false
        try {
            if ($scope -eq 'local') { Assert-Wiring ($local.Replace($name, 'missing.ps1')) $ci }
            else { Assert-Wiring $local ($ci.Replace($name, 'missing.ps1')) }
        }
        catch { $rejected = $true }
        if (-not $rejected) { throw "Removing $name from $scope passed." }
    }
}
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$scratch = Join-Path $tempRoot ('keyringguard-orchestration-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
try {
    $native = Join-Path $scratch 'native-failure.ps1'
    [IO.File]::WriteAllText($native, 'exit 23')
    $ignore = Join-Path $scratch 'ignore'
    [IO.File]::WriteAllText($ignore, '')
    [IO.File]::WriteAllText((Join-Path $scratch 'Directory.Build.props'), '<Project><PropertyGroup><Version>0.1.0</Version></PropertyGroup></Project>')
    [IO.File]::WriteAllText((Join-Path $scratch 'Directory.Packages.props'), '<Project><ItemGroup><PackageVersion Include="KeelMatrix.KeyRingGuard" Version="0.1.0" /></ItemGroup></Project>')
    [IO.File]::WriteAllText((Join-Path $scratch 'CHANGELOG.md'), "# Changelog`n## [Unreleased]`n## [0.1.0] - Planned (not yet published)`n### Added`n- Provides verification.")
    $cases = @(
        @{ File = $native; Arguments = @() },
        @{ File = (Join-Path $root 'scripts/Test-IdeIgnoreContract.ps1'); Arguments = @('-IgnorePath', $ignore) },
        @{ File = (Join-Path $root 'scripts/Test-ChangelogContract.ps1'); Arguments = @('-Tag', 'v0.1.0', '-RepositoryRoot', $scratch, '-ChangelogPath', (Join-Path $scratch 'CHANGELOG.md'), '-PackageVersionPath', (Join-Path $scratch 'Directory.Build.props'), '-CentralPackageVersionPath', (Join-Path $scratch 'Directory.Packages.props')) },
        @{ File = (Join-Path $root 'scripts/Publish-ReleaseArtifacts.ps1'); Arguments = @('-Version', '0.1.0', '-PackageDirectory', $scratch, '-DryRun') }
    )
    foreach ($case in $cases) {
        $argumentsPath = Join-Path $scratch 'arguments.json'
        [IO.File]::WriteAllText($argumentsPath, (ConvertTo-Json -InputObject @($case.Arguments)))
        $marker = Join-Path $scratch 'following-operation'
        $null = @(Invoke-NestedPwsh -NoProfile -File (Join-Path $PSScriptRoot 'Invoke-GateFailureProbe.ps1') -CommandFile $case.File -ArgumentsPath $argumentsPath -MarkerPath $marker 2>&1)
        if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $marker)) { throw 'A required failure advanced to a later operation.' }
    }
    Write-Output 'Gate orchestration: PASS (8 removal controls; native, changelog, ignore, and publisher failures stop subsequent work).'
}
finally {
    if (-not ([IO.Path]::GetFullPath($scratch)).StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture cleanup path.' }
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
exit 0
