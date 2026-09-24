[CmdletBinding()]
param(
    [string]$WorkflowPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..' '.github' 'workflows' 'release.yml'))
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $WorkflowPath -PathType Leaf)) { throw "Release workflow is missing: $WorkflowPath" }
$text = Get-Content -Raw -LiteralPath $WorkflowPath

function Assert-Contains([string]$Value, [string]$Pattern, [string]$Message) {
    if ($Value -notmatch $Pattern) { throw "Release workflow contract failed: $Message" }
}

Assert-Contains $text '(?m)^\s+push:\s*$' 'the workflow must have a push trigger.'
Assert-Contains $text "(?m)^\s+- 'v\*'\s*$" 'the workflow must trigger on v* tags.'
if ($text -match '(?m)^\s+pull_request:|(?m)^\s+branches:') { throw 'Release workflow contract failed: ordinary branch or pull-request triggers are not allowed.' }
Assert-Contains $text 'scripts/GetReleaseVersion\.ps1' 'the workflow must derive the release version from the tag.'
Assert-Contains $text 'scripts/Test-ChangelogContract\.ps1' 'the workflow must recheck changelog/version consistency.'
Assert-Contains $text 'scripts/Invoke-VulnerabilityAudit\.ps1' 'the workflow must run the fail-closed vulnerability audit.'
Assert-Contains $text 'scripts/Inspect-Package\.ps1' 'the workflow must inspect the package before publication.'
Assert-Contains $text 'Validate exact artifact set' 'the validation job must check the exact artifact set.'
Assert-Contains $text 'NuGet/login@v1' 'the publish job must use NuGet Trusted Publishing.'
Assert-Contains $text 'user: dmitriyzen' 'the publish job must use the approved NuGet username.'
Assert-Contains $text 'id-token: write' 'only the publish job should request the OIDC token permission.'
Assert-Contains $text 'dotnet nuget push' 'publication must occur only after validation.'

$onBlock = [regex]::Match($text, '(?ms)^on:\s*(?<body>.*?)(?=^permissions:|\z)').Groups['body'].Value
if ([string]::IsNullOrWhiteSpace($onBlock)) { throw 'Release workflow contract failed: the workflow must declare an on block.' }
if ($onBlock -notmatch '(?m)^push:\s*$') { throw 'Release workflow contract failed: the workflow must use a push trigger for releases.' }
if ($onBlock -notmatch "(?m)^\s+- 'v\*'\s*$") { throw 'Release workflow contract failed: the workflow must trigger only for v* tags.' }
if ($onBlock -match '(?m)^(?!push:\s*$)[A-Za-z_][A-Za-z0-9_-]*:\s*$') { throw 'Release workflow contract failed: only the tag push trigger is allowed.' }
if ($onBlock -match '(?m)^\s{4}(?:branches|branches-ignore|paths|paths-ignore):') { throw 'Release workflow contract failed: branch/path filters are not allowed on the release trigger.' }

Write-Output 'Release workflow contract: PASS (tag-only trigger, fail-closed validation, exact artifacts, and OIDC publication path).'
exit 0
