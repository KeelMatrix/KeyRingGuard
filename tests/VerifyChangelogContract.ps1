[CmdletBinding()]
param(
    [string]$RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')),
    [string]$CaseFilter = '*'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../build/Invoke-NestedPwsh.ps1')
. (Join-Path $PSScriptRoot '../scripts/Invoke-BoundedDotnet.ps1')
. (Join-Path $PSScriptRoot 'New-ChangelogFixture.ps1')
$contract = Join-Path $RepositoryRoot 'scripts/Test-ChangelogContract.ps1'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$scratch = Join-Path $tempRoot ('keyringguard-changelog-' + [guid]::NewGuid().ToString('N'))
$fixture = Join-Path $scratch 'real project with spaces'
$count = 0; $groups = @{}

function Invoke-Contract([string]$Root, [string]$Tag = 'v0.1.0', [switch]$Readiness, [string[]]$Extra = @()) {
    $arguments = @('-NoProfile', '-File', $contract, '-Tag', $Tag, '-RepositoryRoot', $Root)
    if ($Extra -notcontains '-ExpectedCommit') { $arguments += @('-ExpectedCommit', '') }
    if ($Readiness) { $arguments += '-Readiness' }
    $output = @(Invoke-NestedPwsh @arguments @Extra 2>&1)
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output -join [Environment]::NewLine }
}
function Check([string]$Name, [bool]$Pass, [scriptblock]$Change = {}, [switch]$Readiness, [string]$Tag = 'v0.1.0', [string[]]$Extra = @(), [string]$ExpectedError) {
    if ($Name -notlike $CaseFilter -and $Name -ne 'control') { return }
    New-ChangelogFixture $fixture
    & $Change
    $result = Invoke-Contract $fixture $Tag -Readiness:$Readiness -Extra $Extra
    if (($result.ExitCode -eq 0) -ne $Pass) { throw "Changelog case '$Name' expected pass=$Pass; exit=$($result.ExitCode): $($result.Output)" }
    if (-not $Pass -and $result.Output -notmatch 'Changelog contract failed:|release tag|Package probe failed|Package probe exceeded') { throw "Case '$Name' failed for an unintended reason: $($result.Output)" }
    if ($ExpectedError -and $result.Output -notmatch $ExpectedError) { throw "Case '$Name' did not reach its intended boundary: $($result.Output)" }
    $script:count++
    $group = ($Name -split '-')[0]; $script:groups[$group]++
}
function Markdown([string]$Text) { [IO.File]::WriteAllText((Join-Path $fixture 'CHANGELOG.md'), $Text) }
function Xml([string]$Name, [string]$Text) { [IO.File]::WriteAllText((Join-Path $fixture $Name), $Text) }
$prefix = "# Changelog`n`n## [Unreleased]`n`n"
$heading = '## [0.1.0] - 2026-09-26'
$valid = $prefix + $heading + "`n`n### Added`n`n- Provides continuity verification."
$props = '<Project><PropertyGroup><Version>0.1.0</Version><PackageVersion>$(Version)</PackageVersion></PropertyGroup></Project>'
$central = '<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup><ItemGroup><PackageVersion Include="KeelMatrix.KeyRingGuard" Version="0.1.0" /></ItemGroup></Project>'
$project = 'src/KeelMatrix.KeyRingGuard/KeelMatrix.KeyRingGuard.csproj'
try {
    Check 'control' $true
    if (Test-Path (Join-Path $fixture 'src/KeelMatrix.KeyRingGuard/obj')) { throw 'Evaluation-only control ran restore or a target.' }
    # Every required role crosses indentation and opening/closing physical lines.
    foreach ($indent in 0..3) {
        foreach ($layout in @('<!--x-->', "<!--x`n-->", "<!--`nx`n-->")) {
            foreach ($position in @($heading, '### Added', '- Provides continuity verification.', '## [Unreleased]')) {
                $physicalPrefix = (' ' * $indent) + $layout.Replace("`n", "`n" + (' ' * $indent))
                Check "M1-$indent-$layout-$position" $false { Markdown $valid.Replace($position, $physicalPrefix + $position) } -ExpectedError 'block comment closing-line suffix'
            }
        }
        foreach ($comment in @('<!-- example -->', "<!--`nexample`n-->")) {
            Check "M2-real-comment-$indent-$comment" $true { Markdown ($valid + "`n`n" + (' ' * $indent) + $comment + '   ') }
        }
        foreach ($position in @($heading, '### Added', '- Provides continuity verification.', '## [Unreleased]')) {
            Check "M2-comment-only-$indent-$position" $false { Markdown $valid.Replace($position, (' ' * $indent) + '<!--' + $position + '-->') }
        }
        foreach ($position in @($heading, '### Added', '## [Unreleased]')) {
            Check "M5-real-indent-$indent-$position" $true { Markdown $valid.Replace($position, (' ' * $indent) + $position) }
        }
    }
    foreach ($text in @('Provides<!--x--> continuity verification.', 'Provides <!--x-->continuity<!--y--> verification.', 'Provides continuity<!--x--> verification.')) {
        Check "M3-inline-$text" $true { Markdown $valid.Replace('Provides continuity verification.', $text) }
    }
    foreach ($text in @('Pla<!--x-->nned', 'not<!--x-->-yet-published', '<!--x-->Planned', 'Provides<!--x', "Provides<!--x`n--> verification.", 'Provides<!--a<!--b-->c--> verification.', 'Provides --> verification.', 'Provides <!--a--b--> verification.')) {
        Check "M3-invalid-$text" $false { Markdown ($valid + "`n`n- " + $text) }
    }
    $ticks = [string][char]96
    foreach ($marker in @($ticks, '~')) {
        foreach ($length in @(3, 4)) {
            foreach ($closeLength in @(($length - 1), $length, ($length + 1))) {
                foreach ($indent in @(0, 3, 4)) {
                    $literal = (' ' * $indent) + ($marker * $length) + " info<!--`n" + $heading + "`n<!-- literal`n" + (' ' * $indent) + ($marker * $closeLength)
                    $pass = $closeLength -ge $length -and $indent -lt 4
                    Check "M4-fence-$marker-$length-$closeLength-$indent" $pass { Markdown ($valid + "`n`n" + $literal) }
                    Check "M4-fake-only-$marker-$length-$closeLength-$indent" $false { Markdown ($prefix + $literal) }
                }
            }
        }
    }
    foreach ($literal in @(
        "<!--`n~~~`n$heading`n-->",
        "~~~`n<!--`n$heading`n~~~",
        "> $heading`n> <!--`n> Planned",
        "    $heading`n    <!--`n    Planned",
        "`t$heading`n`t<!--`n`tPlanned"
    )) {
        Check "M4-owner-real-$literal" $true { Markdown ($valid + "`n`n" + $literal) }
        Check "M4-owner-only-$literal" $false { Markdown ($prefix + $literal) }
    }
    foreach ($bad in @(
        "`n    <!--", "`n   ~~~`nx`n   ~~~", "`n> <!--", "`n`n> example`nPlanned",
        "`n`n    example`nPlanned", "`n`n~~~`nx`n~~", "`n`n<!--x", "`n`n<!--x--> plain", "`n`n<!--x--> <!--y-->",
        ("`n`n" + ($ticks * 3) + ' info' + $ticks + "`n" + ($ticks * 3))
    )) { Check "M4-boundary-$bad" $false { Markdown ($valid + $bad) } }
    foreach ($position in @($heading, '### Added', '## [Unreleased]')) {
        foreach ($indent in @('    ', "`t")) { Check "M5-code-heading-$position-$indent" $false { Markdown $valid.Replace($position, $indent + $position) } }
    }
    foreach ($level in @(1, 3, 4, 5, 6)) { Check "M5-target-level-$level" $false { Markdown $valid.Replace($heading, ('#' * $level) + ' [0.1.0] - 2026-09-26') } }
    foreach ($bad in @(
        ($valid + "`n`n" + $heading),
        ($prefix + "### Container`n#### [0.1.0] - 2026-09-26`n##### Added`n- Provides verification."),
        ($valid + "`n`n#### [0.2.0] - 2026-09-26"),
        $valid.Replace('### Added', '#### Added'), $valid.Replace('### Added', '### Added ###'),
        $valid.Replace('### Added', '### Ad<!--x-->ded'), ($valid + "`n`n# Changelog"),
        ($valid + "`n`n[0.1.0] - 2026-09-26`n---------------------")
    )) { Check "M5-hierarchy-$bad" $false { Markdown $bad } }
    Check 'M5-full-supplemental' $true { Markdown ($valid + "`n`n#### Detail`n##### More`n###### Notes`nProvides bounded verification.") }
    foreach ($body in @('', '- <!--x-->', '- ...', '- ``', "~~~`n- Provides verification.`n~~~", '> - Provides verification.', '    - Provides verification.')) {
        Check "M6-no-content-$body" $false { Markdown ($prefix + $heading + "`n`n### Added`n`n" + $body) }
    }
    Check 'M6-content-before-category' $false { Markdown ($prefix + $heading + "`n`n- Provides verification.`n`n### Added") }
    foreach ($category in @('Fixed', 'Changed', "Added`n`n### Fixed", "Added`n`n### Added")) { Check "M6-category-$category" $false { Markdown $valid.Replace('### Added', '### ' + $category) } }
    foreach ($bullet in @('- ', '* ', '+ ')) { Check "M6-bullet-$bullet" $true { Markdown $valid.Replace('- Provides', $bullet + 'Provides') } }
    Check 'M6-substantive-soft-wrap' $true { Markdown $valid.Replace('- Provides continuity verification.', "- ...`nProvides continuity verification.") }
    foreach ($body in @('-  Provides verification.', '  - Provides verification.', '1. Provides verification.', '- [x] Provides verification.')) { Check "M6-list-$body" $false { Markdown ($prefix + $heading + "`n`n### Added`n`n" + $body) } }
    foreach ($text in @('Pl&#97;nned', 'Pl&#x61;nned', 'not&nbsp;yet published', 'not&#32;yet&#x20;published', 'Provides &amp; verifies.', 'Provides &#65; verification.')) {
        Check "I1-$text" $false { Markdown ($valid + "`n`n- " + $text) }
    }
    foreach ($text in @('Pla*nn*ed', 'Pla**nn**ed', 'Pla_nn_ed', 'Pla__nn__ed', 'Pla<em>nn</em>ed', 'not yet pub<span>lish</span>ed', '**Provides** verification.', '<em>Provides</em> verification.', 'Pla**n*ned', '<span><em>Provides</em></span> verification.')) {
        Check "I2-$text" $false { Markdown ($valid + "`n`n- " + $text) }
    }
    foreach ($text in @('`Planned`', 'Pla`nn`ed', 'not `yet` published', '`not-yet-published`', '` net8.0`', '`net8.0 `', '``net8.0``', '`net8.0', "`u{0060}net`n8.0`u{0060}")) {
        Check "I3-invalid-$text" $false { Markdown ($valid + "`n`n- " + $text) }
    }
    foreach ($text in @('Targets `net8.0`.', 'Documents `<!--` delimiters.', 'Documents `&amp;` bytes.', 'Documents `<span>text</span>` bytes.', 'Documents `*` and `_` bytes.')) {
        Check "I3-supported-$text" $true { Markdown ($valid + "`n`n- " + $text) }
    }
    foreach ($text in @('PLANNED', 'unreleased', 'Pending', 'TBD', 'unpublished', 'not published', 'not-yet-published', "not`nyet`npublished", 'pre release', 'prerelease', 'pre-release', 'Pla<!--x-->`nn`ed', 'no-longer', 'used to', 'changed-from', 'previously', 'formerly', 'fixed', 'fixes', 'corrected', 'resolved', 'addressed', 'this removes', 'this-fixes', "no`nlonger", "Provides`u{200b} verification.", "Provides`u{00a0}verification.", 'Provides [a](link).', 'Provides | verification.', ('Provides ' + [char]92 + '*verification.'))) {
        Check "I4-$text" $false { Markdown ($valid + "`n`n- " + $text) }
    }
    foreach ($mode in @($false, $true)) {
        Check "R3-final-$mode" $true -Readiness:$mode
        Check "R3-draft-narrative-$mode" $mode { Markdown ($valid + "`n- Planned release; not yet published.") } -Readiness:$mode
        Check "R3-draft-$mode" $mode { Markdown $valid.Replace('2026-09-26', 'Planned (not yet published)') } -Readiness:$mode
        Check "R3-malformed-$mode" $false { Markdown $valid.Replace('2026-09-26', 'Planned') } -Readiness:$mode
        Check "R3-grammar-$mode" $false { Markdown $valid.Replace($heading, '<!--x-->' + $heading) } -Readiness:$mode
        Check "R3-version-$mode" $false { Xml 'Directory.Build.props' $props.Replace('0.1.0', '0.1.1') } -Readiness:$mode
        Check "R3-editorial-$mode" $false { Markdown ($valid + "`n- Now provides verification.") } -Readiness:$mode
    }
    Check 'I4-multiple-separators' $false { Markdown ($valid + "`n- Pre  release.") }
    foreach ($date in @('2024-02-29', [datetime]::UtcNow.ToString('yyyy-MM-dd'))) { Check "R2-valid-$date" $true { Markdown $valid.Replace('2026-09-26', $date) } }
    foreach ($date in @('2026-02-29', '2026-02-30', '2026-13-01', '2026-00-01', '2026-09-00', '2026-9-26', '2026-09-26 pending', [datetime]::UtcNow.AddDays(1).ToString('yyyy-MM-dd'))) { Check "R2-invalid-$date" $false { Markdown $valid.Replace('2026-09-26', $date) } }
    foreach ($newline in @("`n", "`r`n")) { foreach ($terminal in @('', $newline)) { Check "R2-lines-$newline-$terminal" $true { Markdown ($valid.Replace("`n", $newline) + $terminal) } } }
    Check 'R2-bom' $true { [IO.File]::WriteAllText((Join-Path $fixture 'CHANGELOG.md'), $valid, [Text.UTF8Encoding]::new($true)) }
    Check 'R2-bare-cr' $false { Markdown $valid.Replace("`n", "`r") }
    Check 'R2-invalid-utf8' $false { [IO.File]::WriteAllBytes((Join-Path $fixture 'CHANGELOG.md'), [byte[]]@(0xc3, 0x28)) }
    foreach ($tag in @('v00.1.0', '0.1.0', 'v0.1', 'v0.1.1', 'v0.1.0-preview', 'v0.1.0+build', 'v-1.1.0', 'v65535.1.0')) { Check "R1-tag-$tag" $false -Tag $tag }
    foreach ($version in @('0.0.0', '1.1.1', '65534.65534.65534')) {
        Check "R1-range-$version" $true { New-ChangelogFixture $fixture $version } -Tag ('v' + $version)
    }
    foreach ($version in @('65535.0.0', '00.1.0', '-1.1.0', '0.1', '0.1.0-preview', '0.1.1')) { Check "R1-source-$version" $false { Xml 'Directory.Build.props' $props.Replace('0.1.0', $version) } }
    # Real XML, including comments that must never supply a declaration.
    foreach ($file in @('Directory.Build.props', 'Directory.Packages.props')) {
        $original = if ($file -eq 'Directory.Build.props') { $props } else { $central }
        $declaration = if ($file -eq 'Directory.Build.props') { '<Version>0.1.0</Version>' } else { '<PackageVersion Include="KeelMatrix.KeyRingGuard" Version="0.1.0" />' }
        foreach ($comment in @(('<!--' + $declaration + '-->'), "<!--`n$declaration`n-->")) {
            Check "X1-sole-$file-$comment" $false { Xml $file $original.Replace($declaration, $comment) }
            foreach ($value in @('0.1.0', '0.1.1')) { Check "X1-decoy-$file-$value-$comment" $true { Xml $file $original.Replace('</Project>', $comment.Replace('0.1.0', $value) + '</Project>') } }
        }
        Check "X1-format-$file" $true { Xml $file $original.Replace('><', ">`n  <").Replace('"', "'") }
        foreach ($location in @('leaf', 'ancestor')) {
            foreach ($condition in @('', 'true', 'false')) {
                $changed = if ($condition -eq '') { $original } else {
                    $needle = if ($location -eq 'leaf') { if ($file -eq 'Directory.Build.props') { '<Version' } else { '<PackageVersion' } } else { if ($file -eq 'Directory.Build.props') { '<PropertyGroup' } else { '<ItemGroup' } }
                    $original.Replace($needle, $needle + ' Condition="' + $condition + '"')
                }
                Check "X2-condition-$file-$location-$condition" ($condition -eq '') { Xml $file $changed }
            }
        }
        foreach ($wrapper in @('<Choose><When Condition="true">', '<Target Name="VersionTarget">')) {
            $close = if ($wrapper.StartsWith('<Choose')) { '</When></Choose>' } else { '</Target>' }
            $group = if ($file -eq 'Directory.Build.props') { 'PropertyGroup' } else { 'ItemGroup' }
            Check "X2-position-$file-$wrapper" $false { Xml $file ('<Project>' + $wrapper + '<' + $group + '>' + $declaration + '</' + $group + '>' + $close + '</Project>') }
        }
        Check "X2-namespace-$file" $false { Xml $file $original.Replace('<Project>', '<Project xmlns="urn:version">') }
        Check "X2-case-$file" $false { Xml $file $original.Replace('Version', 'version') }
        Check "X3-missing-$file" $false { Xml $file $original.Replace($declaration, '') }
        foreach ($value in @('0.1.0', '0.1.1')) { Check "X3-duplicate-$file-$value" $false { Xml $file $original.Replace($declaration, $declaration + $declaration.Replace('0.1.0', $value)) } }
    }
    foreach ($value in @('$(Other)', '$([System.String]::Copy(0.1.0))', '@(Version)', '<![CDATA[0.1.0]]>', '0.1.<Patch>0</Patch>', '0.1.<!--x-->0')) {
        Check "X3-derived-$value" $false { Xml 'Directory.Build.props' $props.Replace('>0.1.0<', '>' + $value + '<') }
    }
    Check 'X3-missing-alias' $false { Xml 'Directory.Build.props' $props.Replace('<PackageVersion>$(Version)</PackageVersion>', '') }
    Check 'X3-duplicate-alias' $false { Xml 'Directory.Build.props' $props.Replace('<PackageVersion>$(Version)</PackageVersion>', '<PackageVersion>$(Version)</PackageVersion><PackageVersion>$(Version)</PackageVersion>') }
    foreach ($value in @('0.1.0', '$(Other)', '$(Version)<!--x-->')) { Check "X3-alias-$value" $false { Xml 'Directory.Build.props' $props.Replace('$(Version)', $value) } }
    foreach ($node in @('<VersionPrefix>0.1.1</VersionPrefix>', '<VersionSuffix>preview</VersionSuffix>')) { Check "X3-prefix-$node" $false { Xml 'Directory.Build.props' $props.Replace('</PropertyGroup>', $node + '</PropertyGroup>') } }
    foreach ($item in @('<PackageVersion Update="KeelMatrix.KeyRingGuard" Version="0.1.0" />', '<PackageVersion Remove="KeelMatrix.KeyRingGuard" />', '<PackageVersion Include="KeelMatrix.KeyRingGuard"><Version>0.1.0</Version></PackageVersion>', '<PackageVersion Include="KeelMatrix.*" Version="0.1.0" />', '<PackageVersion Include="$(Id)" Version="0.1.0" />', '<PackageVersion Include="KeelMatrix.KeyRingGuard;Other" Version="0.1.0" />', '<PackageVersion Include="KeelMatrix.KeyRingGuard" Version="$(Version)" />')) {
        Check "X3-central-$item" $false { Xml 'Directory.Packages.props' $central.Replace('<PackageVersion Include="KeelMatrix.KeyRingGuard" Version="0.1.0" />', $item) }
    }
    foreach ($xml in @('<Project><!-- <Version>0.1.0</Version> --><PropertyGroup><VersionPrefix>0.1.1</VersionPrefix></PropertyGroup></Project>', '<Project><PropertyGroup Condition="false"><Version>0.1.0</Version></PropertyGroup><PropertyGroup><VersionPrefix>0.1.1</VersionPrefix></PropertyGroup></Project>')) {
        Check "X4-baseline-$xml" $false { Xml 'Directory.Build.props' $xml }
        if ($CaseFilter -eq '*' -or 'X4-baseline-evaluation' -like $CaseFilter) {
            New-ChangelogFixture $fixture; Xml 'Directory.Build.props' $xml
            Push-Location $fixture
            try { $actual = (Invoke-BoundedDotnet -Arguments @('msbuild', (Join-Path $fixture $project), '-nologo', '-getProperty:Version') | Out-String).Trim(); if ($actual -cne '0.1.1') { throw 'Baseline fixture did not actually evaluate to 0.1.1.' } }
            finally { Pop-Location }
        }
    }
    Check 'X4-project-override' $false { Xml $project ([IO.File]::ReadAllText((Join-Path $fixture $project)).Replace('</PropertyGroup>', '<Version>0.1.1</Version></PropertyGroup>')) }
    foreach ($file in @('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props')) {
        Check "X4-shadow-$file" $false { Xml ('src/' + $file) '<Project />' }
        Remove-Item -LiteralPath (Join-Path $fixture ('src/' + $file)) -Force -ErrorAction SilentlyContinue
    }
    Check 'X4-unused-path' $false -Extra @('-PackageVersionPath', 'unused.props') { Xml 'unused.props' $props }
    Check 'X4-central-mismatch' $false { Xml 'Directory.Packages.props' $central.Replace('0.1.0', '0.1.1') }
    $savedHook = $env:CustomAfterMicrosoftCommonTargets
    try {
        $hook = Join-Path $scratch 'evaluation-hook.targets'
        [IO.File]::WriteAllText($hook, '<Project><PropertyGroup><Version>0.1.1</Version><PackageVersion>0.1.1</PackageVersion></PropertyGroup></Project>')
        $env:CustomAfterMicrosoftCommonTargets = $hook
        Check 'X4-actual-evaluated-mismatch' $false -ExpectedError 'evaluated Release package identity differs'
    }
    finally { $env:CustomAfterMicrosoftCommonTargets = $savedHook }
    Check 'X5-evaluation-error' $false { Xml $project ([IO.File]::ReadAllText((Join-Path $fixture $project)).Replace('</PropertyGroup>', '<EvaluationProbe>$([System.Int32]::Parse("invalid"))</EvaluationProbe></PropertyGroup>')) }
    # These are real pinned evaluations, with no targets or substituted evaluator.
    # The opt-in function setting belongs only to this disposable process family.
    $savedFunctions = $env:MSBUILDENABLEALLPROPERTYFUNCTIONS
    try {
        $env:MSBUILDENABLEALLPROPERTYFUNCTIONS = '1'
        Check 'X5-invalid-json' $false {
            Xml $project ([IO.File]::ReadAllText((Join-Path $fixture $project)).Replace('</PropertyGroup>', '<EvaluationNoise>$([System.Console]::WriteLine("evaluation-noise"))</EvaluationNoise></PropertyGroup>'))
        } -ExpectedError 'evaluation did not return valid identity JSON'
        Check 'X5-timeout' $false {
            Xml $project ([IO.File]::ReadAllText((Join-Path $fixture $project)).Replace('</PropertyGroup>', '<EvaluationDelay>$([System.Threading.Thread]::Sleep(61000))</EvaluationDelay></PropertyGroup>'))
        } -ExpectedError 'Package probe exceeded its deadline'
    }
    finally { $env:MSBUILDENABLEALLPROPERTYFUNCTIONS = $savedFunctions }
    Check 'X5-missing-sdk' $false { Xml 'global.json' '{"sdk":{"version":"0.0.0","rollForward":"disable","allowPrerelease":false}}' }
    $savedCentralImport = $env:ImportDirectoryPackagesProps
    try {
        $env:ImportDirectoryPackagesProps = 'false'
        Check 'X5-missing-evaluated-item' $false {
            Xml $project ([IO.File]::ReadAllText((Join-Path $fixture $project)).Replace('</PropertyGroup>', '<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>'))
        } -ExpectedError 'evaluated central shipping item is missing'
    }
    finally { $env:ImportDirectoryPackagesProps = $savedCentralImport }
    foreach ($selector in @('DirectoryBuildPropsPath', 'DirectoryBuildTargetsPath', 'DirectoryPackagesPropsPath', 'CustomAfterMicrosoftCommonTargets')) {
        Check "X4-selector-$selector" $false { Xml 'Directory.Build.props' $props.Replace('</PropertyGroup>', '<' + $selector + '>other.props</' + $selector + '></PropertyGroup>') }
    }
    # XML element identity is DOM metadata, independent of a Name attribute.
    Check 'X1-named-pack-target' $true {
        Xml 'Directory.Build.targets' '<Project><Target Name="ValidateInputs" BeforeTargets="Pack"><Error Condition="false" Text="Must not execute during evaluation." /><ItemGroup><_Input Include="example" /></ItemGroup></Target></Project>'
    }
    foreach ($child in @('<Output TaskParameter="Value" PropertyName="Version" />', '<UnsupportedTask Name="PropertyGroup" />')) {
        Check "X3-named-target-child-$child" $false { Xml 'Directory.Build.targets' ('<Project><Target Name="VersionTarget">' + $child + '</Target></Project>') }
    }
    Check 'X3-dom-attributes-condition' $false { Xml 'Directory.Build.props' $props.Replace('<Version>', '<Version Attributes="" Condition="false">') } -ExpectedError 'conditional version declarations'
    Check 'X3-dom-leaf-content' $false { Xml 'Directory.Build.props' $props.Replace('<Version>0.1.0</Version>', '<Version><Child NodeType="Text">0.1.0</Child></Version>') } -ExpectedError 'Version must contain literal text only'
    Remove-Item -LiteralPath (Join-Path $fixture 'Directory.Build.targets') -Force -ErrorAction SilentlyContinue
    foreach ($xml in @('<Project>', '<!DOCTYPE Project [<!ENTITY version "0.1.0">]><Project><PropertyGroup><Version>&version;</Version></PropertyGroup></Project>', '<?probe x?><Project />', '<Project><Import Project="other.props" /></Project>')) { Check "X5-xml-$xml" $false { Xml 'Directory.Build.props' $xml } }
    if ($CaseFilter -eq '*' -or 'R1-commit' -like $CaseFilter) {
        New-ChangelogFixture $fixture
        $null = & git -C $fixture init -q
        $null = & git -C $fixture -c user.name=Fixture -c user.email=fixture@example.invalid -c commit.gpgsign=false commit --allow-empty -qm 'Initialize fixture'
        if ($LASTEXITCODE -ne 0) { throw 'Cannot initialize expected-commit fixture.' }
        $head = (& git -C $fixture rev-parse HEAD | Out-String).Trim()
        foreach ($expected in @($head, ('0' * 40))) {
            $result = Invoke-Contract $fixture -Extra @('-ExpectedCommit', $expected)
            if (($result.ExitCode -eq 0) -ne ($expected -ceq $head)) { throw "Expected-commit boundary failed: $($result.Output)" }; $count++
        }
    }
    Write-Output "Changelog contract: PASS ($count real child cases; groups=$($groups | ConvertTo-Json -Compress); filter=$CaseFilter)."
}
finally {
    if (-not ([IO.Path]::GetFullPath($scratch)).StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture cleanup path.' }
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
exit 0
