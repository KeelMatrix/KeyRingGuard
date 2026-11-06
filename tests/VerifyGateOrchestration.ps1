[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../build/Invoke-NestedPwsh.ps1')
. (Join-Path $PSScriptRoot 'New-ChangelogFixture.ps1')
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
    $steps = @($ast.EndBlock.Statements | Where-Object {
        $_ -is [Management.Automation.Language.PipelineAst] -and $_.PipelineElements.Count -eq 1 -and
        $_.PipelineElements[0] -is [Management.Automation.Language.CommandAst] -and $_.PipelineElements[0].GetCommandName() -ceq 'Invoke-GateStep'
    })
    if ($steps.Count -eq 0 -or $steps[0].Extent.Text -notmatch "^Invoke-GateStep 'SDK selection' .*Test-SdkSelection[.]ps1") { throw 'Local SDK verification must precede evaluation and every CLI command.' }
    foreach ($name in $required) {
        $matches = @($steps | Where-Object { $_.Extent.Text -match ("^Invoke-GateStep '[^']+' [{] & [(]Join-Path [$]root 'tests' '" + [regex]::Escape($name) + "'[)] [}]$") })
        if ($matches.Count -ne 1) { throw "Local gate must run $name once as a direct checked step." }
        $step = [regex]::Match($CiText, '(?ms)^      - name:[^\r\n]*\r?\n(?:(?!^      - name:).)*run: pwsh -NoProfile -File tests/' + [regex]::Escape($name) + '(?:(?!^      - name:).)*')
        if (-not $step.Success -or $step.Value -match '(?m)^        (?:if|continue-on-error):' -or $step.Value -notmatch ('(?m)^        run: pwsh -NoProfile -File tests/' + [regex]::Escape($name) + '\r?$')) { throw "CI must run $name unconditionally and propagate failure." }
    }
}
foreach ($mutant in @(
    $local.Replace("Invoke-GateStep 'Changelog contract tests'", "if (`$false) { } else { } # Invoke-GateStep 'Changelog contract tests'"),
    $local.Replace("Invoke-GateStep 'SDK selection'", "# Invoke-GateStep 'SDK selection'"),
    $local.Replace("'VerifyChangelogContract.ps1') }", "'VerifyChangelogContract.ps1'); `$global:LASTEXITCODE = 0 }")
)) {
    $rejected = $false; try { Assert-Wiring $mutant $ci } catch { $rejected = $true }
    if (-not $rejected) { throw 'Skipped, masked or late local validation passed.' }
}
foreach ($suffix in @(' || true', "`n        if: false", "`n        continue-on-error: true")) {
    $rejected = $false
    try { Assert-Wiring $local ($ci.Replace('run: pwsh -NoProfile -File tests/VerifyChangelogContract.ps1', 'run: pwsh -NoProfile -File tests/VerifyChangelogContract.ps1' + $suffix)) } catch { $rejected = $true }
    if (-not $rejected) { throw 'Skipped or masked CI fixtures passed.' }
}
Assert-Wiring $local $ci
$sdkLine = [regex]::Match($local, "(?m)^Invoke-GateStep 'SDK selection' [^\r\n]+\r?\n").Value
$lateSdk = $local.Replace($sdkLine, '') + [Environment]::NewLine + $sdkLine
$rejected = $false; try { Assert-Wiring $lateSdk $ci } catch { $rejected = $true }
if (-not $rejected) { throw 'SDK verification after evaluation passed.' }
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
    New-ChangelogFixture $scratch
    [IO.File]::WriteAllText((Join-Path $scratch 'CHANGELOG.md'), "# Changelog`n## [Unreleased]`n## [0.1.0] - Planned (not yet published)`n### Added`n- Provides verification.")
    $cases = @(
        @{ File = $native; Arguments = @() },
        @{ File = (Join-Path $root 'scripts/Test-IdeIgnoreContract.ps1'); Arguments = @('-IgnorePath', $ignore) },
        @{ File = (Join-Path $root 'scripts/Test-ChangelogContract.ps1'); Arguments = @('-Tag', 'v0.1.0', '-RepositoryRoot', $scratch, '-ExpectedCommit', '') },
        @{ File = (Join-Path $root 'scripts/Publish-ReleaseArtifacts.ps1'); Arguments = @('-Version', '0.1.0', '-PackageDirectory', $scratch, '-DryRun') }
    )
    foreach ($case in $cases) {
        $argumentsPath = Join-Path $scratch 'arguments.json'
        [IO.File]::WriteAllText($argumentsPath, (ConvertTo-Json -InputObject @($case.Arguments)))
        $marker = Join-Path $scratch 'following-operation'
        $null = @(Invoke-NestedPwsh -NoProfile -File (Join-Path $PSScriptRoot 'Invoke-GateFailureProbe.ps1') -CommandFile $case.File -ArgumentsPath $argumentsPath -MarkerPath $marker 2>&1)
        if ($LASTEXITCODE -eq 0 -or (Test-Path -LiteralPath $marker)) { throw 'A required failure advanced to a later operation.' }
    }
    $arguments = @('-Tag', 'v0.1.0', '-RepositoryRoot', $scratch, '-ExpectedCommit', '')
    $argumentsPath = Join-Path $scratch 'arguments.json'
    [IO.File]::WriteAllText($argumentsPath, (ConvertTo-Json -InputObject $arguments))
    $validator = Join-Path $root 'scripts/Test-ChangelogContract.ps1'
    $finalized = "# Changelog`n`n## [Unreleased]`n`n## [0.1.0] - 2026-09-26`n`n### Added`n`n- Provides continuity verification."
    foreach ($name in @('ghost', 'encoded', 'inactive', 'evaluated', 'valid')) {
        New-ChangelogFixture $scratch
        $text = switch ($name) {
            'ghost' { $finalized.Replace('## [0.1.0]', '<!--x-->## [0.1.0]') }
            'encoded' { $finalized + "`n- Pl&#97;nned release." }
            default { $finalized }
        }
        [IO.File]::WriteAllText((Join-Path $scratch 'CHANGELOG.md'), $text)
        if ($name -eq 'inactive') { [IO.File]::WriteAllText((Join-Path $scratch 'Directory.Build.props'), '<Project><PropertyGroup Condition="false"><Version>0.1.0</Version></PropertyGroup><PropertyGroup><VersionPrefix>0.1.1</VersionPrefix></PropertyGroup></Project>') }
        $savedHook = $env:CustomAfterMicrosoftCommonTargets
        try {
            if ($name -eq 'evaluated') {
                $hook = Join-Path $scratch 'mismatch.targets'
                [IO.File]::WriteAllText($hook, '<Project><PropertyGroup><Version>0.1.1</Version><PackageVersion>0.1.1</PackageVersion></PropertyGroup></Project>')
                $env:CustomAfterMicrosoftCommonTargets = $hook
            }
            $marker = Join-Path $scratch ("following-$name")
            $null = @(Invoke-NestedPwsh -NoProfile -File (Join-Path $PSScriptRoot 'Invoke-GateFailureProbe.ps1') -CommandFile $validator -ArgumentsPath $argumentsPath -MarkerPath $marker 2>&1)
            if (($LASTEXITCODE -eq 0) -ne ($name -eq 'valid') -or (Test-Path -LiteralPath $marker) -ne ($name -eq 'valid')) { throw "Strict $name did not enforce the following-operation boundary." }
        }
        finally { $env:CustomAfterMicrosoftCommonTargets = $savedHook }
    }
    Write-Output 'Gate orchestration: PASS (removal/skip/masking/order controls; real strict ghost/state/source/evaluation failures stop following operations; valid strict control advances).'
}
finally {
    if (-not ([IO.Path]::GetFullPath($scratch)).StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture cleanup path.' }
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
exit 0
