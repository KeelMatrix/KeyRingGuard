[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Tag,
    [string]$ChangelogPath = (Join-Path $PSScriptRoot '..\CHANGELOG.md'),
    [string]$PackageVersionPath = (Join-Path $PSScriptRoot '..\Directory.Build.props'),
    [string]$CentralPackageVersionPath = (Join-Path $PSScriptRoot '..\Directory.Packages.props'),
    [string]$RepositoryRoot = (Join-Path $PSScriptRoot '..'),
    [string]$ExpectedCommit = ''
)

$ErrorActionPreference = 'Stop'

function Fail([string]$Message) { throw "Changelog contract failed: $Message" }

function Get-CanonicalVersion([string]$Value, [string]$Description) {
    $version = $Value.Trim()
    $component = '(?:0|[1-9]\d{0,3}|[1-5]\d{4}|6[0-4]\d{3}|65[0-4]\d{2}|655[0-2]\d|6553[0-4])'
    if ($version -notmatch "^$component\.$component\.$component$") {
        Fail "$Description '$version' is not a canonical X.Y.Z version."
    }
    return $version
}

function Get-DeclaredVersion([string]$Text, [string]$Description) {
    $match = [regex]::Match($Text, '(?is)<Version\b[^>]*>(?<value>[^<]+)</Version>')
    if (-not $match.Success) { Fail "$Description does not declare Version." }
    return Get-CanonicalVersion $match.Groups['value'].Value $Description
}

function Get-CentralPackageVersion([string]$Text) {
    $match = [regex]::Match($Text, '(?is)<PackageVersion\b(?=[^>]*\bInclude\s*=\s*["'']KeelMatrix\.KeyRingGuard["''])(?=[^>]*\bVersion\s*=\s*["''](?<version>[^"'']+)["''])[^>]*/?>')
    if (-not $match.Success) { Fail 'Directory.Packages.props does not declare the shipping package version.' }
    return Get-CanonicalVersion $match.Groups['version'].Value 'Central package version'
}

if (-not (Test-Path -LiteralPath $RepositoryRoot -PathType Container)) { Fail "Repository root '$RepositoryRoot' does not exist." }
foreach ($path in @($ChangelogPath, $PackageVersionPath, $CentralPackageVersionPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { Fail "Required release-contract file '$path' does not exist." }
}

$releaseVersion = (& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'GetReleaseVersion.ps1') -Tag $Tag | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($releaseVersion)) { Fail "Release tag '$Tag' is invalid." }
$releaseVersion = Get-CanonicalVersion $releaseVersion 'Release tag version'
$packageVersion = Get-DeclaredVersion (Get-Content -Raw -LiteralPath $PackageVersionPath) 'Directory.Build.props'
$centralVersion = Get-CentralPackageVersion (Get-Content -Raw -LiteralPath $CentralPackageVersionPath)
if ($packageVersion -cne $releaseVersion) { Fail "Directory.Build.props version '$packageVersion' does not match release tag '$releaseVersion'." }
if ($centralVersion -cne $releaseVersion) { Fail "Central package version '$centralVersion' does not match release tag '$releaseVersion'." }

if (-not [string]::IsNullOrWhiteSpace($ExpectedCommit)) {
    $head = (& git -C $RepositoryRoot rev-parse HEAD).Trim()
    if ($head -cne $ExpectedCommit.Trim()) { Fail "checked-out commit '$head' does not match expected release commit '$($ExpectedCommit.Trim())'." }
}

$lines = Get-Content -LiteralPath $ChangelogPath
$headingPattern = '^(?<marks>#{1,6})\s+(?<title>.+?)\s*$'
$targets = @()
for ($index = 0; $index -lt $lines.Count; $index++) {
    $heading = [regex]::Match($lines[$index], $headingPattern)
    if (-not $heading.Success) { continue }
    $title = $heading.Groups['title'].Value.Trim()
    $versionMatch = [regex]::Match($title, '^\[(?<version>[^\]]+)\](?:\s*-\s*(?<suffix>.*))?$')
    if ($versionMatch.Success -and $versionMatch.Groups['version'].Value -ceq $releaseVersion) {
        $targets += [pscustomobject]@{ Index = $index; Level = $heading.Groups['marks'].Value.Length; Title = $title; Suffix = $versionMatch.Groups['suffix'].Value.Trim() }
    }
}
if ($targets.Count -ne 1) { Fail "release version '$releaseVersion' must have exactly one changelog release heading." }
$target = $targets[0]
if ($target.Title -match '(?i)\b(planned|unreleased|tbd|not\s+yet\s+published|not\s+published|pre[-\s]?release)\b') { Fail "release heading '$($target.Title)' still uses pre-release wording." }

for ($index = $target.Index - 1; $index -ge 0; $index--) {
    $ancestor = [regex]::Match($lines[$index], $headingPattern)
    if (-not $ancestor.Success) { continue }
    if ($ancestor.Groups['marks'].Value.Length -lt $target.Level) {
        if ($ancestor.Groups['title'].Value -match '(?i)\bunreleased\b') { Fail "release version '$releaseVersion' is nested inside Unreleased." }
        break
    }
}

$dateMatch = [regex]::Match($target.Suffix, '^(?<date>\d{4}-\d{2}-\d{2})(?:\s|$)')
if (-not $dateMatch.Success) { Fail "release heading '$($target.Title)' must contain a release date in yyyy-MM-dd form." }
[datetime]$releaseDate = [datetime]::MinValue
if (-not [datetime]::TryParseExact($dateMatch.Groups['date'].Value, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$releaseDate)) {
    Fail "release date '$($dateMatch.Groups['date'].Value)' is invalid."
}
if ($releaseDate.Date -gt [datetime]::UtcNow.Date) { Fail "release date '$($dateMatch.Groups['date'].Value)' is later than the current UTC date." }

Write-Output "Changelog contract passed for $Tag ($releaseVersion)."
