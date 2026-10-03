[CmdletBinding()]
param(
    [string]$WorkflowPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..' '.github' 'workflows' 'release.yml'))
)

$ErrorActionPreference = 'Stop'
$regexEscape = [char]92
$whitespace = $regexEscape + 's'
$literalStar = $regexEscape + '*'
$literalDot = $regexEscape + '.'
$endOfString = $regexEscape + 'z'
if (-not (Test-Path -LiteralPath $WorkflowPath -PathType Leaf)) { throw "Release workflow is missing: $WorkflowPath" }
$text = Get-Content -Raw -LiteralPath $WorkflowPath

function Assert-Contains([string]$Value, [string]$Pattern, [string]$Message) {
    if ($Value -notmatch $Pattern) { throw "Release workflow contract failed: $Message" }
}

Assert-Contains $text ('(?m)^' + $whitespace + '+push:' + $whitespace + '*$') 'the workflow must have a push trigger.'
Assert-Contains $text ("(?m)^" + $whitespace + "+- 'v" + $literalStar + "'" + $whitespace + "*$") 'the workflow must trigger on v* tags.'
if ($text -match ('(?m)^' + $whitespace + '+pull_request:|(?m)^' + $whitespace + '+branches:')) { throw 'Release workflow contract failed: ordinary branch or pull-request triggers are not allowed.' }
Assert-Contains $text ('scripts/GetReleaseVersion' + $literalDot + 'ps1') 'the workflow must derive the release version from the tag.'
Assert-Contains $text ('scripts/Test-ChangelogContract' + $literalDot + 'ps1') 'the workflow must recheck changelog/version consistency.'
Assert-Contains $text ('scripts/Invoke-VulnerabilityAudit' + $literalDot + 'ps1') 'the workflow must run the fail-closed vulnerability audit.'
Assert-Contains $text ('scripts/Inspect-Package' + $literalDot + 'ps1') 'the workflow must inspect the package before publication.'
Assert-Contains $text ('scripts/Test-ReleaseArtifactSet' + $literalDot + 'ps1') 'the workflow must validate exact artifacts in both release jobs.'
Assert-Contains $text '-p:RequirePackageIcon=true' 'release packing must fail closed when the founder-owned icon is absent.'
Assert-Contains $text 'Validate exact artifact set' 'the validation job must check the exact artifact set.'
Assert-Contains $text 'NuGet/login@v1' 'the publish job must use NuGet Trusted Publishing.'
Assert-Contains $text 'user: dmitriyzen' 'the publish job must use the approved NuGet username.'
Assert-Contains $text 'id-token: write' 'only the publish job should request the OIDC token permission.'
Assert-Contains $text ('scripts/Publish-ReleaseArtifacts' + $literalDot + 'ps1') 'publication must invoke the committed artifact-publishing script after validation.'
Assert-Contains $text 'dotnet format KeelMatrix.KeyRingGuard.sln --no-restore --verify-no-changes' 'release validation must run the pinned-SDK formatting gate.'
Assert-Contains $text 'dotnet-version: 10.0.401' 'release validation must install the SDK pinned by global.json.'

$publishStart = $text.IndexOf('      - name: Publish validated package', [StringComparison]::Ordinal)
if ($publishStart -lt 0) { throw 'Release workflow contract failed: the publish step is missing.' }
$publishScriptPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..' 'scripts' 'Publish-ReleaseArtifacts.ps1'))
if (-not (Test-Path -LiteralPath $publishScriptPath -PathType Leaf)) {
    throw "Release workflow contract failed: publish script is missing: $publishScriptPath"
}
$publishScript = Get-Content -Raw -LiteralPath $publishScriptPath
if ($publishScript -notmatch '(?m)dotnet\s+nuget\s+push') {
    throw 'Release workflow contract failed: the committed publish script does not push NuGet artifacts.'
}

$onBlock = [regex]::Match($text, ('(?ms)^on:' + $whitespace + '*(?<body>.*?)(?=^permissions:|' + $endOfString + ')')).Groups['body'].Value
if ([string]::IsNullOrWhiteSpace($onBlock)) { throw 'Release workflow contract failed: the workflow must declare an on block.' }
if ($onBlock -notmatch ('(?m)^push:' + $whitespace + '*$')) { throw 'Release workflow contract failed: the workflow must use a push trigger for releases.' }
if ($onBlock -notmatch ("(?m)^" + $whitespace + "+- 'v" + $literalStar + "'" + $whitespace + "*$")) { throw 'Release workflow contract failed: the workflow must trigger only for v* tags.' }
if ($onBlock -match ('(?m)^(?!push:' + $whitespace + '*$)[A-Za-z_][A-Za-z0-9_-]*:' + $whitespace + '*$')) { throw 'Release workflow contract failed: only the tag push trigger is allowed.' }
if ($onBlock -match ('(?m)^' + $whitespace + '{4}(?:branches|branches-ignore|paths|paths-ignore):')) { throw 'Release workflow contract failed: branch/path filters are not allowed on the release trigger.' }

Write-Output 'Release workflow contract: PASS (tag-only trigger, fail-closed validation, exact artifacts, and OIDC publication path).'
exit 0
