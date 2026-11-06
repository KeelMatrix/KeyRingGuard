[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../build/Invoke-NestedPwsh.ps1')
$contract = Join-Path $RepositoryRoot 'scripts' 'Test-ChangelogContract.ps1'
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('keyringguard-changelog-contract-' + [Guid]::NewGuid().ToString('N'))

function Invoke-Contract([string]$Root, [string]$Tag, [switch]$Readiness) {
    $extra = @()
    if ($Readiness) { $extra += '-Readiness' }
    $output = @(Invoke-NestedPwsh -NoProfile -File $contract -Tag $Tag -RepositoryRoot $Root -ChangelogPath (Join-Path $Root 'CHANGELOG.md') -PackageVersionPath (Join-Path $Root 'Directory.Build.props') -CentralPackageVersionPath (Join-Path $Root 'Directory.Packages.props') @extra 2>&1)
    [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join [Environment]::NewLine).Trim() }
}

function Write-Fixture([string]$Root, [string]$Heading, [string]$Version = '0.1.0', [string]$CentralVersion = '0.1.0') {
    New-Item -ItemType Directory -Force -Path $Root | Out-Null
    Set-Content -LiteralPath (Join-Path $Root 'Directory.Build.props') -Value "<Project><PropertyGroup><Version>$Version</Version></PropertyGroup></Project>" -NoNewline
    $centralProps = '<Project><ItemGroup><PackageVersion Include="KeelMatrix.KeyRingGuard" Version="' + $CentralVersion + '" /></ItemGroup></Project>'
    Set-Content -LiteralPath (Join-Path $Root 'Directory.Packages.props') -Value $centralProps -NoNewline
    Set-Content -LiteralPath (Join-Path $Root 'CHANGELOG.md') -Value "# Changelog`n`n## [Unreleased]`n`n$Heading`n`n- Synthetic release notes.`n" -NoNewline
}

New-Item -ItemType Directory -Force -Path $scratch | Out-Null
try {
    $planned = Join-Path $scratch 'planned'
    Write-Fixture $planned '## [0.1.0] - Planned (not yet published)'
    $result = Invoke-Contract $planned 'v0.1.0'
    if ($result.ExitCode -eq 0) { throw 'A planned release heading must fail.' }

    $finalized = Join-Path $scratch 'finalized'
    Write-Fixture $finalized "## [0.1.0] - $((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd'))`n`n### Added`n`n- Provides the documented continuity verification path."
    $result = Invoke-Contract $finalized 'v0.1.0'
    if ($result.ExitCode -ne 0) { throw "A finalized release heading should pass: $($result.Output)" }

    $nonAdded = Join-Path $scratch 'non-added'
    Write-Fixture $nonAdded "## [0.1.0] - $((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd'))`n`n### Fixed`n`n- Corrected an unreleased implementation detail."
    $result = Invoke-Contract $nonAdded 'v0.1.0'
    if ($result.ExitCode -eq 0) { throw 'A first release with a non-Added section must fail.' }

    $remediation = Join-Path $scratch 'remediation'
    Write-Fixture $remediation "## [0.1.0] - $((Get-Date).ToUniversalTime().ToString('yyyy-MM-dd'))`n`n### Added`n`n- The package now verifies continuity."
    $result = Invoke-Contract $remediation 'v0.1.0'
    if ($result.ExitCode -eq 0) { throw 'A first-release remediation marker must fail.' }

    $mismatch = Join-Path $scratch 'mismatch'
    Write-Fixture $mismatch "## [0.1.0] - 2026-09-24`n`n### Added`n`n- Provides the documented continuity verification path." -CentralVersion '0.1.1'
    $result = Invoke-Contract $mismatch 'v0.1.0'
    if ($result.ExitCode -eq 0) { throw 'A central package version mismatch must fail.' }

    $fixture = Join-Path $scratch 'matrix'
    Write-Fixture $fixture '## [0.1.0] - 2026-09-26'
    $valid = "## [0.1.0] - 2026-09-26`n### Added`n- Provides continuity verification."
    $prefix = "# Changelog`n## [Unreleased]`n"
    $cases = [Collections.Generic.List[object]]::new()
    function Case([string]$Name, [string]$Text, [bool]$Pass = $false, [string]$Tag = 'v0.1.0') {
        $cases.Add(@{ Name = $Name; Text = $Text; Pass = $Pass; Tag = $Tag })
    }
    foreach ($state in @('Planned release', 'UNRELEASED', 'pending', 'TBD', 'not published', 'not **yet** published', "not`nyet`npublished", 'pre-release')) {
        foreach ($position in @('paragraph', 'bullet', 'nested')) {
            $body = switch ($position) { 'bullet' { "- $state" } 'nested' { "#### Status`n$state" } default { $state } }
            Case "state-$position-$state" ($prefix + $valid + "`n" + $body)
        }
    }
    foreach ($level in 1..6 | Where-Object { $_ -ne 2 }) {
        Case "level-$level" ($prefix + ('#' * $level) + " [0.1.0] - 2026-09-26`n" + ('#' * ($level + 1)) + " Added`n- Provides verification.")
    }
    Case 'direct-unreleased' ($prefix + "### [0.1.0] - 2026-09-26`n#### Added`n- Provides verification.")
    Case 'indirect-unreleased' ($prefix + "### Candidate`n#### [0.1.0] - 2026-09-26`n##### Added`n- Provides verification.")
    Case 'duplicate' ($prefix + $valid + "`n" + $valid)
    Case 'missing' $prefix
    Case 'empty-added' ($prefix + "## [0.1.0] - 2026-09-26`n### Added")
    Case 'nested-release' ($prefix + $valid + "`n#### [0.2.0] - 2026-09-26`n- Provides another release.")
    foreach ($suffix in @('2026-02-30', '9999-12-31', '2026-09-26 Planned', '2026-09-26 pending', '2026-9-26')) {
        Case "date-$suffix" ($prefix + $valid.Replace('2026-09-26', $suffix))
    }
    foreach ($tag in @('v00.1.0', '0.1.0', 'v0.1', 'v0.1.1', 'v0.1.0-preview')) { Case "tag-$tag" ($prefix + $valid) $false $tag }
    $ticks = [string][char]96
    foreach ($literal in @(
        (($ticks * 3) + " markdown`n" + $valid + "`n" + ($ticks * 4)),
        ("  ~~~ text`n" + $valid + "`n  ~~~~"),
        (($ticks * 3) + " markdown<!--`n" + $valid + "`n" + ($ticks * 3)),
        ("~~~" + $ticks + "info<!--`n" + $valid + "`n~~~"),
        ("<!--`n" + $valid + "`n-->"),
        ('<!--' + $valid.Replace("`n", "`n") + '-->'),
        (($valid -split "`n" | ForEach-Object { '    ' + $_ }) -join "`n"),
        (($valid -split "`n" | ForEach-Object { '> ' + $_ }) -join "`n")
    )) {
        Case 'literal-only' ($prefix + $literal)
        Case 'literal-duplicate-with-real' ($prefix + $valid + "`n" + $literal) $true
    }
    Case 'unterminated-comment' ($prefix + $valid + "`n<!-- unfinished")
    Case 'unterminated-fence' ($prefix + $valid + "`n~~~ unfinished")
    Case 'short-fence-close' ($prefix + $valid + "`n~~~~`nexample`n~~~")
    Case 'indented-comment-example' ($prefix + $valid + "`n    <!--") $true
    Case 'quoted-comment-example' ($prefix + $valid + "`n> <!--") $true
    Case 'fenced-comment-delimiter' ($prefix + $valid + "`n~~~`n<!--`n~~~") $true
    Case 'commented-fence' ($prefix + $valid + "`n<!-- ~~~ -->") $true
    Case 'inline-comment' ($prefix + $valid.Replace('continuity', 'continuity<!-- example -->')) $true
    Case 'comment-split-state' ($prefix + $valid + "`n- Pla<!-- example -->nned release.")
    Case 'raw-html-example' ($prefix + "<pre>`n" + $valid + "`n</pre>")
    Case 'setext-duplicate' ($prefix + $valid + "`n[0.1.0] - 2026-09-26`n---------------------")
    Case 'current-date' ($prefix + $valid.Replace('2026-09-26', [datetime]::UtcNow.ToString('yyyy-MM-dd'))) $true
    foreach ($newline in @("`n", "`r`n")) {
        foreach ($terminal in @('', $newline)) { Case 'line-ending' (($prefix + $valid).Replace("`n", $newline) + $terminal) $true }
    }
    foreach ($case in $cases) {
        [IO.File]::WriteAllText((Join-Path $fixture 'CHANGELOG.md'), $case.Text)
        $result = Invoke-Contract $fixture $case.Tag
        if (($result.ExitCode -eq 0) -ne $case.Pass) { throw "Changelog case '$($case.Name)' expected pass=$($case.Pass): $($result.Output)" }
    }
    [IO.File]::WriteAllText((Join-Path $fixture 'CHANGELOG.md'), $prefix + $valid.Replace('2026-09-26', 'Planned (not yet published)'))
    if ((Invoke-Contract $fixture 'v0.1.0' -Readiness).ExitCode -ne 0) { throw 'Draft readiness must pass.' }
    if ((Invoke-Contract $fixture 'v0.1.0').ExitCode -eq 0) { throw 'Draft readiness must never satisfy the strict tag gate.' }
    foreach ($version in @('0.1.1', '00.1.0')) {
        Write-Fixture $fixture $valid -Version $version
        if ((Invoke-Contract $fixture 'v0.1.0').ExitCode -eq 0) { throw 'An invalid/mismatched package version passed.' }
    }
    Write-Fixture $fixture $valid
    foreach ($file in @('Directory.Build.props', 'Directory.Packages.props')) {
        $path = Join-Path $fixture $file
        $original = [IO.File]::ReadAllText($path)
        $duplicate = if ($file -ceq 'Directory.Build.props') { '<Version>0.1.1</Version>' } else { '<PackageVersion Include="KeelMatrix.KeyRingGuard" Version="0.1.1" />' }
        [IO.File]::WriteAllText($path, $original.Replace('</Project>', $duplicate + '</Project>'))
        if ((Invoke-Contract $fixture 'v0.1.0').ExitCode -eq 0) { throw 'Ambiguous duplicate version declarations passed.' }
        [IO.File]::WriteAllText($path, $original)
    }
    $head = (& git -C $RepositoryRoot rev-parse HEAD | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot establish the commit fixture.' }
    foreach ($expected in @($head, ('0' * 40))) {
        $null = @(Invoke-NestedPwsh -NoProfile -File $contract -Tag v0.1.0 -RepositoryRoot $RepositoryRoot -ExpectedCommit $expected -ChangelogPath (Join-Path $fixture 'CHANGELOG.md') -PackageVersionPath (Join-Path $fixture 'Directory.Build.props') -CentralPackageVersionPath (Join-Path $fixture 'Directory.Packages.props') 2>&1)
        if (($LASTEXITCODE -eq 0) -ne ($expected -ceq $head)) { throw 'Expected-commit matching did not fail closed.' }
    }
    Write-Output "Changelog contract: PASS ($($cases.Count + 13) executable cases)."
exit 0
}
finally {
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
