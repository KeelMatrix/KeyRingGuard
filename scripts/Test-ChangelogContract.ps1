[CmdletBinding()]
param(
    [string]$Tag = $env:RELEASE_TAG,
    [string]$ChangelogPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..' 'CHANGELOG.md')),
    [string]$PackageVersionPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..' 'Directory.Build.props')),
    [string]$CentralPackageVersionPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..' 'Directory.Packages.props')),
    [string]$RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')),
    [string]$ExpectedCommit = $env:EXPECTED_COMMIT,
    # Readiness checks draft structure only. The tag workflow must omit this switch.
    [switch]$Readiness
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Tag)) { throw 'A release tag is required through -Tag or RELEASE_TAG.' }
. (Join-Path $PSScriptRoot '../build/Invoke-NestedPwsh.ps1')
$wordBoundary = [char]92 + 'b'
$whitespace = [char]92 + 's'
$literalOpenBracket = [char]92 + '['
$literalCloseBracket = [char]92 + ']'

function Fail([string]$Message) { throw "Changelog contract failed: $Message" }

function Get-CanonicalVersion([string]$Value, [string]$Description) {
    $version = $Value.Trim()
    $component = '(?:0|[1-9][0-9]{0,3}|[1-5][0-9]{4}|6[0-4][0-9]{3}|65[0-4][0-9]{2}|655[0-2][0-9]|6553[0-4])'
    if ($version -notmatch "^$component[.]$component[.]$component$") {
        Fail "$Description '$version' is not a canonical X.Y.Z version."
    }
    return $version
}

function Get-DeclaredVersion([string]$Text, [string]$Description) {
    $matches = [regex]::Matches($Text, ('(?is)<Version' + $wordBoundary + '[^>]*>(?<value>[^<]+)</Version>'))
    if ($matches.Count -ne 1) { Fail "$Description must declare exactly one Version." }
    return Get-CanonicalVersion $matches[0].Groups['value'].Value $Description
}

function Get-CentralPackageVersion([string]$Text) {
    $matches = [regex]::Matches($Text, ('(?is)<PackageVersion' + $wordBoundary + '(?=[^>]*' + $wordBoundary + 'Include' + $whitespace + '*=' + $whitespace + '*["'']KeelMatrix[.]KeyRingGuard["''])(?=[^>]*' + $wordBoundary + 'Version' + $whitespace + '*=' + $whitespace + '*["''](?<version>[^"'']+)["''])[^>]*/?>'))
    if ($matches.Count -ne 1) { Fail 'Directory.Packages.props must declare exactly one shipping package version.' }
    return Get-CanonicalVersion $matches[0].Groups['version'].Value 'Central package version'
}

if (-not (Test-Path -LiteralPath $RepositoryRoot -PathType Container)) { Fail "Repository root '$RepositoryRoot' does not exist." }
foreach ($path in @($ChangelogPath, $PackageVersionPath, $CentralPackageVersionPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { Fail "Required release-contract file '$path' does not exist." }
}

$releaseVersion = (Invoke-NestedPwsh -NoProfile -File (Join-Path $PSScriptRoot 'GetReleaseVersion.ps1') -Tag $Tag -OutputPath '' | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($releaseVersion)) { Fail "Release tag '$Tag' is invalid." }
$releaseVersion = Get-CanonicalVersion $releaseVersion 'Release tag version'
$packageVersion = Get-DeclaredVersion (Get-Content -Raw -LiteralPath $PackageVersionPath) 'Directory.Build.props'
$centralVersion = Get-CentralPackageVersion (Get-Content -Raw -LiteralPath $CentralPackageVersionPath)
if ($packageVersion -cne $releaseVersion) { Fail "Directory.Build.props version '$packageVersion' does not match release tag '$releaseVersion'." }
if ($centralVersion -cne $releaseVersion) { Fail "Central package version '$centralVersion' does not match release tag '$releaseVersion'." }

if (-not [string]::IsNullOrWhiteSpace($ExpectedCommit)) {
    $head = (& git -C $RepositoryRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { Fail 'cannot determine the checked-out commit.' }
    if ($head -cne $ExpectedCommit.Trim()) { Fail "checked-out commit '$head' does not match expected release commit '$($ExpectedCommit.Trim())'." }
}

function Get-VisibleMarkdown([string[]]$InputLines) {
    $comment = $false
    $fenceCharacter = ''
    $fenceLength = 0
    $openingFence = '^ {0,3}(?<fence>' + [char]96 + '{3,}|~{3,})(?<info>.*)$'
    foreach ($line in $InputLines) {
        if ($fenceLength -gt 0) {
            if ($line -match ('^ {0,3}' + [regex]::Escape($fenceCharacter) + '{' + $fenceLength + ',}[ ' + [char]9 + ']*$')) {
                $fenceLength = 0
            }
            ''
            continue
        }
        if (-not $comment -and $line -match ('^(?: {4}|[' + [char]9 + ']| {0,3}>)')) { ''; continue }
        # A fence's info string is literal too: its comment delimiters have no HTML meaning.
        $rawFence = [regex]::Match($line, $openingFence)
        if (-not $comment -and $rawFence.Success) {
            $fenceCharacter = $rawFence.Groups['fence'].Value.Substring(0, 1)
            if ($fenceCharacter -ceq [string][char]96 -and $rawFence.Groups['info'].Value.Contains([char]96)) { Fail 'unsupported backtick fence info string.' }
            $fenceLength = $rawFence.Groups['fence'].Value.Length
            ''
            continue
        }
        $visible = [Text.StringBuilder]::new()
        $position = 0
        while ($position -lt $line.Length) {
            if ($comment) {
                $close = $line.IndexOf('-->', $position, [StringComparison]::Ordinal)
                if ($close -lt 0) { break }
                $comment = $false
                $position = $close + 3
            }
            else {
                $open = $line.IndexOf('<!--', $position, [StringComparison]::Ordinal)
                if ($open -lt 0) { [void]$visible.Append($line.Substring($position)); break }
                [void]$visible.Append($line.Substring($position, $open - $position))
                $comment = $true
                $position = $open + 4
            }
        }
        $text = $visible.ToString()
        if ($text.TrimStart().StartsWith('<', [StringComparison]::Ordinal)) { Fail 'raw HTML blocks are outside the supported changelog grammar.' }
        $fence = [regex]::Match($text, $openingFence)
        if ($fence.Success) {
            $fenceCharacter = $fence.Groups['fence'].Value.Substring(0, 1)
            if ($fenceCharacter -ceq [string][char]96 -and $fence.Groups['info'].Value.Contains([char]96)) { Fail 'unsupported backtick fence info string.' }
            $fenceLength = $fence.Groups['fence'].Value.Length
            ''
        }
        elseif ($text -match ('^(?: {4}|[' + [char]9 + ']| {0,3}>)')) { '' }
        else { $text }
    }
    if ($comment -or $fenceLength -gt 0) { Fail 'unterminated comment or fenced example.' }
}

# Supported grammar: real ATX H1/H2/H3 headings outside complete literal regions.
# Releases and the separate Unreleased section are H2 siblings, with direct categories.
$lines = @(Get-VisibleMarkdown @(Get-Content -LiteralPath $ChangelogPath))
for ($index = 0; $index -lt $lines.Count - 1; $index++) {
    if ($lines[$index].TrimStart().StartsWith('[', [StringComparison]::Ordinal) -and $lines[$index + 1].Trim() -match '^(?:=+|-+)$') {
        Fail 'Setext release headings are outside the supported changelog grammar; use level-two ATX entries.'
    }
}
$headingPattern = '^(?<marks>#{1,6})' + $whitespace + '+(?<title>.+?)' + $whitespace + '*$'
$targets = @()
$headings = @()
$stack = [Collections.Generic.List[object]]::new()
for ($index = 0; $index -lt $lines.Count; $index++) {
    $heading = [regex]::Match($lines[$index].TrimStart(' '), $headingPattern)
    if (-not $heading.Success) { continue }
    $title = $heading.Groups['title'].Value.Trim()
    $level = $heading.Groups['marks'].Value.Length
    while ($stack.Count -gt 0 -and $stack[$stack.Count - 1].Level -ge $level) { $stack.RemoveAt($stack.Count - 1) }
    $record = [pscustomobject]@{ Index = $index; Level = $level; Title = $title; Suffix = ''; Ancestors = @($stack.ToArray()) }
    $headings += $record
    $versionMatch = [regex]::Match($title, ('^' + $literalOpenBracket + '(?<version>[^' + $literalCloseBracket + ']+)' + $literalCloseBracket + '(?:' + $whitespace + '*-' + $whitespace + '*(?<suffix>.*))?$'))
    if ($versionMatch.Success -and $versionMatch.Groups['version'].Value -ceq $releaseVersion) {
        $record.Suffix = $versionMatch.Groups['suffix'].Value.Trim()
        $targets += $record
    }
    $stack.Add($record)
}
if ($targets.Count -ne 1) { Fail "release version '$releaseVersion' must have exactly one changelog release heading." }
$target = $targets[0]
foreach ($ancestor in $target.Ancestors) {
    if ($ancestor.Title -match ('(?i)' + $wordBoundary + 'unreleased' + $wordBoundary)) { Fail "release version '$releaseVersion' is nested inside Unreleased." }
}
if ($target.Level -ne 2) { Fail 'release entries must be level-two headings.' }
if (@($headings | Where-Object { $_.Level -eq 2 -and $_.Title -ceq '[Unreleased]' }).Count -ne 1) { Fail 'exactly one separate level-two [Unreleased] section is required.' }

$draft = $Readiness -and $target.Suffix -ceq 'Planned (not yet published)'
$dateMatch = [regex]::Match($target.Suffix, '^(?<date>[0-9]{4}-[0-9]{2}-[0-9]{2})$')
if (-not $draft) {
if (-not $dateMatch.Success) { Fail "release heading '$($target.Title)' must contain a release date in yyyy-MM-dd form." }
[datetime]$releaseDate = [datetime]::MinValue
if (-not [datetime]::TryParseExact($dateMatch.Groups['date'].Value, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$releaseDate)) {
    Fail "release date '$($dateMatch.Groups['date'].Value)' is invalid."
}
if ($releaseDate.Date -gt [datetime]::UtcNow.Date) { Fail "release date '$($dateMatch.Groups['date'].Value)' is later than the current UTC date." }
}

$sectionEnd = $lines.Count
for ($index = $target.Index + 1; $index -lt $lines.Count; $index++) {
    $heading = [regex]::Match($lines[$index].TrimStart(' '), $headingPattern)
    if ($heading.Success -and $heading.Groups['marks'].Value.Length -le $target.Level) {
        $sectionEnd = $index
        break
    }
}

$releaseText = ($lines[$target.Index..($sectionEnd - 1)] -join [Environment]::NewLine)
if (-not $Readiness) {
    $stateText = $releaseText.Replace('*', '').Replace('_', '').Replace([string][char]96, '')
    if ($stateText -match ('(?i)' + $wordBoundary + '(planned|unreleased|pending|tbd|not' + $whitespace + '+(?:yet' + $whitespace + '+)?published|pre[-' + $whitespace + ']?release)' + $wordBoundary)) {
        Fail 'the target entry contains unfinished release state.'
    }
}
if ($releaseText -notmatch ('(?m)^ {0,3}[-*+] +' + [char]92 + 'S')) { Fail 'the release entry needs substantive capability content.' }
foreach ($heading in $headings | Where-Object { $_.Index -gt $target.Index -and $_.Index -lt $sectionEnd }) {
    if ($heading.Title -match ('^' + $literalOpenBracket)) { Fail 'nested release entries are unsupported.' }
}
if ($releaseVersion -ceq '0.1.0') {
    $directSections = @()
    for ($index = $target.Index + 1; $index -lt $sectionEnd; $index++) {
        $heading = [regex]::Match($lines[$index].TrimStart(' '), $headingPattern)
        if ($heading.Success -and $heading.Groups['marks'].Value.Length -eq ($target.Level + 1)) {
            $directSections += $heading.Groups['title'].Value.Trim()
        }
    }

    if ($directSections.Count -ne 1 -or $directSections[0] -cne 'Added') {
        Fail "first release '$releaseVersion' must contain exactly one direct 'Added' section; found: $($directSections -join ', ')."
    }

    $remediationPatterns = @(
        ($wordBoundary + 'now' + $wordBoundary)
        ($wordBoundary + 'no' + $whitespace + '+' + 'longer' + $wordBoundary)
        ($wordBoundary + 'previously' + $wordBoundary)
        ($wordBoundary + 'formerly' + $wordBoundary)
        ($wordBoundary + 'used' + $whitespace + '+' + 'to' + $wordBoundary)
        ($wordBoundary + 'fixed' + $wordBoundary)
        ($wordBoundary + 'fixes' + $wordBoundary)
        ($wordBoundary + 'corrected' + $wordBoundary)
        ($wordBoundary + 'resolved' + $wordBoundary)
        ($wordBoundary + 'addressed' + $wordBoundary)
        ($wordBoundary + 'this' + $whitespace + '+' + 'removes' + $wordBoundary)
        ($wordBoundary + 'this' + $whitespace + '+' + 'fixes' + $wordBoundary)
        ($wordBoundary + 'changed' + $whitespace + '+' + 'from' + $wordBoundary)
    )
    foreach ($pattern in $remediationPatterns) {
        $match = [regex]::Match($releaseText, ('(?i)' + $pattern))
        if ($match.Success) { Fail "first release '$releaseVersion' contains unpublished remediation wording '$($match.Value)'." }
    }
}

Write-Output "Changelog contract passed for $Tag ($releaseVersion)."
