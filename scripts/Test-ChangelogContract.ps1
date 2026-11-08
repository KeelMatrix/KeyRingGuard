<#
.SYNOPSIS
Validates the finite changelog grammar and real Release package identity.
.DESCRIPTION
Reads raw blocks before projecting supported inline text. Unsupported syntax fails
closed in both modes. See docs/releasing.md and tests/VerifyChangelogContract.ps1.
Requires the exact global.json SDK for releases; readiness permits servicing patches.
Evaluates the shipping project without targets,
restore, or version overrides. Paths must describe the selected repository graph.
.PARAMETER RepositoryRoot
Root containing global.json, Directory.Build.props and Directory.Packages.props.
.PARAMETER Tag
Canonical vX.Y.Z identity, or RELEASE_TAG when omitted.
.PARAMETER ChangelogPath
Within-root changelog, defaulting to CHANGELOG.md.
.PARAMETER PackageVersionPath
Must select the root Directory.Build.props.
.PARAMETER CentralPackageVersionPath
Must select the root Directory.Packages.props.
.PARAMETER ShippingProjectPath
Within-root SDK project; defaults to src/KeelMatrix.KeyRingGuard/KeelMatrix.KeyRingGuard.csproj.
.PARAMETER ExpectedCommit
Optional exact fixture/repository HEAD, or EXPECTED_COMMIT when omitted.
.PARAMETER Readiness
Also permits the exact planned heading, draft narrative and local SDK servicing
patches. Never use on a tag path.
#>
[CmdletBinding()]
param(
    [string]$Tag = $env:RELEASE_TAG,
    [string]$ChangelogPath,
    [string]$PackageVersionPath,
    [string]$CentralPackageVersionPath,
    [string]$RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')),
    [string]$ShippingProjectPath,
    [string]$ExpectedCommit = $env:EXPECTED_COMMIT,
    [switch]$Readiness
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../build/Invoke-NestedPwsh.ps1')
. (Join-Path $PSScriptRoot 'Invoke-BoundedDotnet.ps1')
. (Join-Path $PSScriptRoot '../build/SdkSelection.ps1')
$rx = [string][char]92

function Fail([string]$Message) { throw "Changelog contract failed: $Message" }

function Get-CanonicalVersion([string]$Value, [string]$Description) {
    if ($Value -cnotmatch '^(0|[1-9][0-9]*)[.](0|[1-9][0-9]*)[.](0|[1-9][0-9]*)$') { Fail "$Description is not a canonical X.Y.Z version." }
    foreach ($part in $Value.Split('.')) {
        if ($part.Length -gt 5 -or [int]$part -gt 65534) { Fail "$Description has an out-of-range version component." }
    }
    return $Value
}

function Resolve-ContractPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path, $RepositoryRoot)
    $relative = [IO.Path]::GetRelativePath($RepositoryRoot, $full)
    if ([IO.Path]::IsPathRooted($relative) -or $relative -eq '..' -or $relative.StartsWith('..' + [IO.Path]::DirectorySeparatorChar)) { Fail 'contract paths must remain inside RepositoryRoot.' }
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { Fail "required contract file '$relative' is missing." }
    $current = $full
    while ($true) {
        if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { Fail 'contract paths cannot use reparse points.' }
        if ($current -eq $RepositoryRoot) { break }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    return $full
}

function Read-PhysicalLines([string]$Path) {
    try { $text = [Text.UTF8Encoding]::new($false, $true).GetString([IO.File]::ReadAllBytes($Path)) }
    catch { Fail 'changelog must be valid UTF8.' }
    if ($text.StartsWith([string][char]0xfeff, [StringComparison]::Ordinal)) { $text = $text.Substring(1) }
    if ($text.Replace("`r`n", '').Contains("`r")) { Fail 'bare carriage returns are unsupported.' }
    return $text.Replace("`r`n", "`n").Split("`n")
}

function Assert-CommentBody([string]$Body) {
    if ($Body.Contains('--') -or $Body.Contains('<!--') -or $Body.EndsWith('-') -or $Body.StartsWith('>') -or $Body.StartsWith('->')) { Fail 'nested or malformed comment.' }
}

# Classify physical blocks once. Inline handling cannot create headings or bullets.
function Read-MarkdownBlocks([string[]]$Lines) {
    $records = [Collections.Generic.List[object]]::new()
    $state = 'Boundary'; $fence = ''; $fenceLength = 0; $commentText = ''
    for ($i = 0; $i -lt $Lines.Count; $i++) {
        $line = $Lines[$i]
        if ($state -eq 'Fence') {
            if ($line -cmatch ('^ {0,3}' + [regex]::Escape($fence) + '{' + $fenceLength + ',} *$')) { $state = 'Boundary' }
            continue
        }
        if ($state -eq 'Comment') {
            $commentText += "`n" + $line
            $close = $commentText.IndexOf('-->', [StringComparison]::Ordinal)
            if ($close -ge 0) {
                if ($commentText.Substring($close + 3).Trim().Length -ne 0) { Fail 'block comment closing-line suffix is unsupported.' }
                Assert-CommentBody $commentText.Substring(0, $close)
                $state = 'Boundary'; $commentText = ''
            }
            continue
        }
        if ($line -cmatch '^ *$') { $state = 'Boundary'; continue }
        if ($state -eq 'Quote') {
            if ($line -cmatch '^ {0,3}>') { continue }
            Fail 'quoted examples require explicit markers and a blank exit boundary.'
        }
        if ($state -eq 'Indented') {
            if ($line.StartsWith('    ') -or $line.StartsWith([string][char]9)) { continue }
            Fail 'indented examples require a blank exit boundary.'
        }
        if ($line.StartsWith('    ') -or $line.StartsWith([string][char]9)) {
            if ($state -ne 'Boundary') { Fail 'indented example attached to prose or a list is ambiguous.' }
            $state = 'Indented'; continue
        }
        if ($line -cmatch '^ {0,3}>') {
            if ($state -ne 'Boundary') { Fail 'quoted example attached to prose or a list is unsupported.' }
            $state = 'Quote'; continue
        }
        $opening = [regex]::Match($line, '^ {0,3}(?<run>`{3,}|~{3,})(?<info>.*)$')
        if ($opening.Success) {
            if ($state -ne 'Boundary' -and $line.StartsWith(' ')) { Fail 'indented fence attached to prose or a list is ambiguous.' }
            $run = $opening.Groups['run'].Value
            if ($run[0] -eq [char]96 -and $opening.Groups['info'].Value.Contains([char]96)) { Fail 'backtick fence info cannot contain backticks.' }
            $fence = [string]$run[0]; $fenceLength = $run.Length; $state = 'Fence'; continue
        }
        if ($line -cmatch '^ {0,3}<!--') {
            $commentText = $line.Substring($line.IndexOf('<!--', [StringComparison]::Ordinal) + 4)
            $close = $commentText.IndexOf('-->', [StringComparison]::Ordinal)
            if ($close -ge 0) {
                if ($commentText.Substring($close + 3).Trim().Length -ne 0) { Fail 'block comment closing-line suffix is unsupported.' }
                Assert-CommentBody $commentText.Substring(0, $close)
                $state = 'Boundary'; $commentText = ''
            }
            else { $state = 'Comment' }
            continue
        }
        $heading = [regex]::Match($line, '^ {0,3}(?<marks>#{1,6}) +(?<title>[^ ].*?) *$')
        if ($heading.Success) {
            if ($state -in @('Paragraph', 'Bullet') -and $line.StartsWith(' ')) { Fail 'indented heading attached to prose or a list is ambiguous.' }
            $title = $heading.Groups['title'].Value
            if ($title -cmatch '[^ -~]|[`&<>*_~|#]' -or $title.Contains([char]92)) { Fail 'heading titles must be literal printable ASCII without inline markup or closing hashes.' }
            $records.Add([pscustomobject]@{ Kind = 'Heading'; Line = $i; Level = $heading.Groups['marks'].Value.Length; Text = $title; Parent = $null })
            $state = 'Boundary'; continue
        }
        if ($line -cmatch '^ *#|^ {1,3}[-*+] |^ *[0-9]+[.)] |^[-=]+ *$') { Fail 'unsupported heading, list or Setext syntax.' }
        $bullet = [regex]::Match($line, '^[-*+] (?<text>[^ ].*)?$')
        if ($line -cmatch '^[-*+] ' -and -not $bullet.Success) { Fail 'flat bullets require exactly one separating space.' }
        if ($line.StartsWith(' ')) { Fail 'indented narrative and container continuations are unsupported.' }
        $kind = if ($bullet.Success) { 'Bullet' } elseif ($state -eq 'Bullet') { 'BulletContinuation' } else { 'Paragraph' }
        $text = if ($bullet.Success) { $bullet.Groups['text'].Value } else { $line }
        $records.Add([pscustomobject]@{ Kind = $kind; Line = $i; Level = 0; Text = $text; Parent = $null })
        $state = if ($bullet.Success -or $state -eq 'Bullet') { 'Bullet' } else { 'Paragraph' }
    }
    if ($state -in @('Fence', 'Comment')) { Fail 'unterminated fenced example or block comment.' }
    return $records.ToArray()
}

function Get-VisibleInline([string]$Text) {
    if ($Text -cmatch '[^ -~]') { Fail 'narrative requires printable ASCII.' }
    $visible = [Text.StringBuilder]::new()
    for ($i = 0; $i -lt $Text.Length;) {
        if ($Text[$i] -eq [char]96) {
            $end = $Text.IndexOf([char]96, $i + 1)
            if ($end -le $i + 1) { Fail 'inline code requires one balanced nonempty single-backtick span.' }
            $code = $Text.Substring($i + 1, $end - $i - 1)
            if ($code -cne $code.Trim() -or ($end + 1 -lt $Text.Length -and $Text[$end + 1] -eq [char]96)) { Fail 'unsupported inline code boundary.' }
            [void]$visible.Append($code); $i = $end + 1; continue
        }
        if ($Text.Substring($i).StartsWith('<!--', [StringComparison]::Ordinal)) {
            $end = $Text.IndexOf('-->', $i + 4, [StringComparison]::Ordinal)
            if ($end -lt 0) { Fail 'inline comments must close on the same physical line.' }
            Assert-CommentBody $Text.Substring($i + 4, $end - $i - 4)
            $i = $end + 3; continue
        }
        if ('&<>*_~[]|'.Contains($Text[$i]) -or $Text[$i] -eq [char]92 -or $Text.Substring($i).StartsWith('-->', [StringComparison]::Ordinal)) { Fail 'unsupported inline syntax (entities, formatting, HTML, links, escapes or stray comments).' }
        [void]$visible.Append($Text[$i]); $i++
    }
    return $visible.ToString()
}

function Assert-MarkdownContract([object[]]$Records, [string]$Version) {
    $stack = [Collections.Generic.List[object]]::new(); $targets = @(); $unreleased = @(); $roots = @()
    foreach ($node in $Records) {
        if ($node.Kind -eq 'Heading') {
            while ($stack.Count -gt 0 -and $stack[$stack.Count - 1].Level -ge $node.Level) { $stack.RemoveAt($stack.Count - 1) }
            if ($node.Level -gt 1 -and ($stack.Count -eq 0 -or $stack[$stack.Count - 1].Level -ne $node.Level - 1)) { Fail 'heading ancestry must be complete.' }
            $node.Parent = if ($stack.Count -gt 0) { $stack[$stack.Count - 1] } else { $null }
            if ($node.Level -eq 1) {
                if ($node.Text -cne 'Changelog') { Fail 'the only H1 must be Changelog.' }; $roots += $node
            }
            elseif ($node.Level -eq 2) {
                if ($node.Text -ceq '[Unreleased]') { $unreleased += $node }
                else {
                    $release = [regex]::Match($node.Text, '^' + $rx + '[(?<version>[^]]+)' + $rx + '] - (?<suffix>.+)$')
                    if (-not $release.Success) { Fail 'unsupported release heading.' }
                    $declared = Get-CanonicalVersion $release.Groups['version'].Value 'Heading version'
                    if ($declared -ceq $Version) { $targets += $node }
                }
            }
            elseif ($node.Level -eq 3) {
                if ($node.Text -cnotin @('Added', 'Changed', 'Deprecated', 'Removed', 'Fixed', 'Security')) { Fail 'unsupported direct release category.' }
            }
            elseif ($node.Text.Contains('[') -or $node.Text.Contains(']')) { Fail 'nested release headings are unsupported.' }
            $stack.Add($node)
        }
        else {
            $node.Parent = if ($stack.Count -gt 0) { $stack[$stack.Count - 1] } else { $null }
            $node.Text = Get-VisibleInline $node.Text
        }
    }
    if ($roots.Count -ne 1 -or $targets.Count -ne 1 -or $unreleased.Count -ne 1) { Fail 'exactly one Changelog, real target H2 and separate Unreleased H2 are required.' }
    $target = $targets[0]
    $suffix = $target.Text.Substring($target.Text.IndexOf('] - ', [StringComparison]::Ordinal) + 4)
    $draft = $Readiness -and $suffix -ceq 'Planned (not yet published)'
    if (-not $draft) {
        [datetime]$date = [datetime]::MinValue
        if ($suffix -cnotmatch '^[0-9]{4}-[0-9]{2}-[0-9]{2}$' -or -not [datetime]::TryParseExact($suffix, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$date) -or $date.Date -gt [datetime]::UtcNow.Date) { Fail 'release heading requires a valid nonfuture yyyy-MM-dd date.' }
    }
    $end = [int]::MaxValue
    foreach ($node in $Records) { if ($node.Line -gt $target.Line -and $node.Kind -eq 'Heading' -and $node.Level -le 2) { $end = $node.Line; break } }
    $entry = @($Records | Where-Object { $_.Line -gt $target.Line -and $_.Line -lt $end })
    $categories = @($entry | Where-Object { $_.Kind -eq 'Heading' -and $_.Level -eq 3 })
    if ($Version -ceq '0.1.0' -and ($categories.Count -ne 1 -or $categories[0].Text -cne 'Added')) { Fail 'first release requires exactly one direct Added category.' }
    $substantive = @($entry | Where-Object {
        if ($_.Kind -notin @('Bullet', 'BulletContinuation') -or $_.Text -cnotmatch '[A-Za-z0-9]') { return $false }
        $ancestor = $_.Parent
        while ($null -ne $ancestor -and $ancestor.Level -gt 3) { $ancestor = $ancestor.Parent }
        return $null -ne $ancestor -and $ancestor.Level -eq 3 -and ($Version -cne '0.1.0' -or $ancestor.Text -ceq 'Added')
    })
    if ($substantive.Count -eq 0) { Fail 'substantive capability bullet beneath the release category is required.' }
    $projection = (@($target.Text) + @($entry | ForEach-Object Text)) -join ' '
    $boundary = $rx + 'b'; $separator = '[ -]+'
    if (-not $Readiness -and $projection -match ('(?i)' + $boundary + '(planned|unreleased|pending|tbd|unpublished|not' + $separator + '(?:yet' + $separator + ')?published|pre(?:[ -]+)?release)' + $boundary)) { Fail 'target contains unfinished release state.' }
    if ($Version -ceq '0.1.0' -and $projection -match ('(?i)' + $boundary + '(now|no' + $separator + 'longer|previously|formerly|used' + $separator + 'to|fixed|fixes|corrected|resolved|addressed|this' + $separator + '(?:removes|fixes)|changed' + $separator + 'from)' + $boundary)) { Fail 'first release contains remediation wording.' }
}

function Read-ProjectXml([string]$Path) {
    $settings = [Xml.XmlReaderSettings]::new(); $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit; $settings.XmlResolver = $null
    $reader = $null
    try {
        $reader = [Xml.XmlReader]::Create($Path, $settings)
        $doc = [Xml.XmlDocument]::new(); $doc.XmlResolver = $null; $doc.Load($reader)
    }
    catch { Fail 'invalid or unsupported project XML.' }
    finally { if ($null -ne $reader) { $reader.Dispose() } }
    if ($doc.get_DocumentElement().get_LocalName() -cne 'Project' -or $doc.SelectNodes('//processing-instruction()').Count -ne 0) { Fail 'unsupported project XML root or processing instruction.' }
    foreach ($node in $doc.SelectNodes('//*')) {
        if ($node.get_NamespaceURI() -ne '' -or $node.get_Prefix() -ne '') { Fail 'XML namespaces are unsupported in version sources.' }
        foreach ($attr in $node.get_Attributes()) { if ($attr.get_NamespaceURI() -ne '') { Fail 'XML namespace attributes are unsupported.' } }
        if ($node.get_LocalName() -ieq 'Import' -or $node.get_LocalName() -ieq 'ImportGroup' -or $node.get_LocalName() -ieq 'Sdk') { Fail 'explicit repository imports and SDK elements are unsupported.' }
        if ($node.get_LocalName() -imatch '^(Choose|When|Otherwise|Output)$') { Fail 'unsupported conditional or output XML schema.' }
        if ($node.get_ParentNode().get_LocalName() -ceq 'Project' -and $node.get_LocalName() -cnotin @('PropertyGroup', 'ItemGroup', 'Target')) { Fail 'unsupported project XML schema.' }
        if ($node.get_ParentNode().get_LocalName() -ceq 'Target' -and $node.get_LocalName() -cnotin @('PropertyGroup', 'ItemGroup', 'Error')) { Fail 'custom version-producing target tasks are unsupported.' }
        if ($node.get_LocalName() -imatch '^(DirectoryBuildPropsPath|DirectoryBuildTargetsPath|DirectoryPackagesPropsPath|ImportDirectoryBuildProps|ImportDirectoryBuildTargets|ImportDirectoryPackagesProps|CustomBeforeMicrosoftCommonProps|CustomAfterMicrosoftCommonProps|CustomBeforeMicrosoftCommonTargets|CustomAfterMicrosoftCommonTargets)$') { Fail 'alternate repository source selection is unsupported.' }
    }
    return ,$doc
}

function Assert-UnconditionalLeaf([Xml.XmlElement]$Node, [string]$Group) {
    if ($Node.get_ParentNode().get_LocalName() -cne $Group -or $Node.get_ParentNode().get_ParentNode().get_LocalName() -cne 'Project') { Fail 'version declaration has unsupported ancestry.' }
    $parent = $Node
    while ($parent -is [Xml.XmlElement]) {
        foreach ($attr in $parent.get_Attributes()) { if ($attr.get_Name() -ieq 'Condition') { Fail 'conditional version declarations are unsupported.' } }
        $parent = $parent.get_ParentNode()
    }
}

function Assert-VersionSources([string]$Project) {
    $props = Join-Path $RepositoryRoot 'Directory.Build.props'; $central = Join-Path $RepositoryRoot 'Directory.Packages.props'
    if ($PackageVersionPath -cne $props -or $CentralPackageVersionPath -cne $central) { Fail 'version paths must select the root source graph.' }
    $directory = [IO.Path]::GetDirectoryName($Project)
    while ($directory -ne $RepositoryRoot) {
        foreach ($name in @('Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props')) { if (Test-Path -LiteralPath (Join-Path $directory $name)) { Fail 'nearer shadow version/build files are unsupported.' } }
        $directory = [IO.Path]::GetDirectoryName($directory)
    }
    $paths = @($props, $central, $Project)
    $targets = Join-Path $RepositoryRoot 'Directory.Build.targets'
    if (Test-Path -LiteralPath $targets) { $paths += (Resolve-ContractPath $targets) }
    $versions = @(); $aliases = @(); $shipping = @()
    foreach ($path in $paths) {
        $doc = Read-ProjectXml $path
        if ($path -eq $Project) {
            if ($doc.get_DocumentElement().get_Attributes().Count -ne 1 -or $doc.get_DocumentElement().GetAttribute('Sdk') -cne 'Microsoft.NET.Sdk') { Fail 'shipping project must select the literal Microsoft.NET.Sdk form.' }
        }
        elseif ($doc.get_DocumentElement().get_Attributes().Count -ne 0) { Fail 'root props/targets attributes are unsupported.' }
        foreach ($node in $doc.SelectNodes('//*')) {
            foreach ($attr in $node.get_Attributes()) {
                if ($attr.get_Name() -in @('PropertyName', 'ItemName') -and $attr.get_Value() -match '^(?i:Version|PackageVersion|VersionPrefix|VersionSuffix)$') { Fail 'target-produced versions are unsupported.' }
            }
            if ($node.get_LocalName() -imatch '^(Version|VersionPrefix|VersionSuffix|PackageVersion)$') {
                if ($node.get_LocalName() -ceq 'Version' -and $path -eq $props) {
                    Assert-UnconditionalLeaf $node 'PropertyGroup'
                    if ($node.get_Attributes().Count -ne 0 -or @($node.get_ChildNodes() | Where-Object { $_.get_NodeType() -ne [Xml.XmlNodeType]::Text }).Count -ne 0) { Fail 'Version must contain literal text only.' }
                    $versions += (Get-CanonicalVersion $node.get_InnerText().Trim() 'Package version')
                }
                elseif ($node.get_LocalName() -ceq 'PackageVersion' -and $node.get_ParentNode().get_LocalName() -ceq 'PropertyGroup' -and $path -eq $props) {
                    Assert-UnconditionalLeaf $node 'PropertyGroup'
                    if ($node.get_Attributes().Count -ne 0 -or $node.get_InnerXml() -cne '$(Version)') { Fail 'only the literal PackageVersion=$(Version) alias is supported.' }; $aliases += $node
                }
                elseif ($node.get_LocalName() -ceq 'PackageVersion' -and $path -eq $central) {
                    Assert-UnconditionalLeaf $node 'ItemGroup'
                    if ($node.get_Attributes().Count -ne 2 -or -not $node.HasAttribute('Include') -or -not $node.HasAttribute('Version') -or $node.get_ChildNodes().Count -ne 0 -or $node.GetAttribute('Include') -cnotmatch '^[A-Za-z0-9_.-]+$') { Fail 'central items require literal Include and Version attributes only.' }
                    $value = Get-CanonicalVersion $node.GetAttribute('Version') 'Central version'
                    if ($node.GetAttribute('Include') -ieq 'KeelMatrix.KeyRingGuard') {
                        if ($node.GetAttribute('Include') -cne 'KeelMatrix.KeyRingGuard') { Fail 'shipping package identity must use canonical casing.' }; $shipping += $value
                    }
                }
                else { Fail 'unsupported, derived, misplaced or duplicate version declaration.' }
            }
        }
    }
    if ($versions.Count -ne 1 -or $aliases.Count -ne 1 -or $shipping.Count -ne 1) { Fail 'exactly one real Version, PackageVersion alias and shipping central item are required.' }
    if ($versions[0] -cne $releaseVersion -or $shipping[0] -cne $releaseVersion) { Fail 'declared package/central versions differ from the tag.' }
}

function Assert-EvaluatedIdentity([string]$Project) {
    $globalPath = Resolve-ContractPath (Join-Path $RepositoryRoot 'global.json')
    try { $sdk = [IO.File]::ReadAllText($globalPath) | ConvertFrom-Json -ErrorAction Stop } catch { Fail 'invalid global.json.' }
    Push-Location $RepositoryRoot
    try {
        $selected = (Invoke-BoundedDotnet -Arguments @('--version') | Out-String).Trim()
        try { Assert-SdkSelection $sdk.sdk $selected -RequireExact:(-not $Readiness) } catch { Fail $_.Exception.Message }
        $json = Invoke-BoundedDotnet -Arguments @('msbuild', $Project, '-nologo', '-p:Configuration=Release', '-getProperty:Version,PackageVersion,PackageId,TargetFramework,ManagePackageVersionsCentrally', '-getItem:PackageVersion')
        try { $evaluation = ($json | Out-String) | ConvertFrom-Json -ErrorAction Stop } catch { Fail 'evaluation did not return valid identity JSON.' }
        $p = $evaluation.Properties
        if ($p.Version -cne $releaseVersion -or $p.PackageVersion -cne $releaseVersion -or $p.PackageId -cne 'KeelMatrix.KeyRingGuard' -or $p.TargetFramework -cne 'net8.0' -or $p.ManagePackageVersionsCentrally -ine 'true') { Fail 'evaluated Release package identity differs from declared identity.' }
        $items = @($evaluation.Items.PackageVersion | Where-Object { $_.Identity -ieq 'KeelMatrix.KeyRingGuard' })
        if ($items.Count -ne 1 -or $items[0].Identity -cne 'KeelMatrix.KeyRingGuard' -or $items[0].Version -cne $releaseVersion -or [IO.Path]::GetFullPath($items[0].DefiningProjectFullPath) -ne $CentralPackageVersionPath) { Fail 'evaluated central shipping item is missing, ambiguous or from another file.' }
    }
    finally { Pop-Location }
}

if ([string]::IsNullOrWhiteSpace($Tag)) { Fail 'a release tag is required.' }
$RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
if (-not (Test-Path -LiteralPath $RepositoryRoot -PathType Container)) { Fail 'RepositoryRoot is missing.' }
if (-not $ChangelogPath) { $ChangelogPath = Join-Path $RepositoryRoot 'CHANGELOG.md' }
if (-not $PackageVersionPath) { $PackageVersionPath = Join-Path $RepositoryRoot 'Directory.Build.props' }
if (-not $CentralPackageVersionPath) { $CentralPackageVersionPath = Join-Path $RepositoryRoot 'Directory.Packages.props' }
if (-not $ShippingProjectPath) { $ShippingProjectPath = Join-Path $RepositoryRoot 'src/KeelMatrix.KeyRingGuard/KeelMatrix.KeyRingGuard.csproj' }
$ChangelogPath = Resolve-ContractPath $ChangelogPath
$PackageVersionPath = Resolve-ContractPath $PackageVersionPath
$CentralPackageVersionPath = Resolve-ContractPath $CentralPackageVersionPath
$ShippingProjectPath = Resolve-ContractPath $ShippingProjectPath
$releaseVersion = (Invoke-NestedPwsh -NoProfile -File (Join-Path $PSScriptRoot 'GetReleaseVersion.ps1') -Tag $Tag -OutputPath '' | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or -not $releaseVersion) { Fail 'invalid release tag.' }
$releaseVersion = Get-CanonicalVersion $releaseVersion 'Release tag'
if ($ExpectedCommit) {
    $head = (& git -C $RepositoryRoot rev-parse HEAD | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $head -cne $ExpectedCommit.Trim()) { Fail 'checked-out commit differs from expected release commit.' }
}
$records = @(Read-MarkdownBlocks @(Read-PhysicalLines $ChangelogPath))
Assert-MarkdownContract $records $releaseVersion
Assert-VersionSources $ShippingProjectPath
Assert-EvaluatedIdentity $ShippingProjectPath
Write-Output "Changelog contract passed for $Tag ($releaseVersion)."
