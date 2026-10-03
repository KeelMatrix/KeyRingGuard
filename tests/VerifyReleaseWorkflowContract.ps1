[CmdletBinding()]
param(
    [string]$WorkflowPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..' '.github' 'workflows' 'release.yml')),
    [string]$PublisherPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..' 'scripts' 'Publish-ReleaseArtifacts.ps1')),
    [string]$ArtifactSetPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..' 'scripts' 'Test-ReleaseArtifactSet.ps1'))
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../build/Invoke-NestedPwsh.ps1')
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
$publishJobStart = $text.IndexOf("  publish:", [StringComparison]::Ordinal)
if ($publishJobStart -lt 0 -or $publishJobStart -gt $publishStart) {
    throw 'Release workflow contract failed: the publish job is missing.'
}
$publishCheckout = $text.IndexOf("        uses: actions/checkout@v4", $publishJobStart, [StringComparison]::Ordinal)
$publishDownload = $text.IndexOf("        uses: actions/download-artifact@v4", $publishJobStart, [StringComparison]::Ordinal)
if ($publishCheckout -lt 0 -or $publishDownload -lt 0 -or $publishCheckout -gt $publishDownload) {
    throw 'Release workflow contract failed: the publish job must check out repository content before downloading artifacts.'
}
$downloadStepStart = $text.IndexOf('      - name: Download validated release artifacts', $publishCheckout, [StringComparison]::Ordinal)
if ($downloadStepStart -lt 0) {
    throw 'Release workflow contract failed: the checkout-removal mutation fixture could not locate the download step.'
}
$checkoutMutation = $text.Remove($publishCheckout, $downloadStepStart - $publishCheckout)
if ($checkoutMutation.Length -ge $text.Length) {
    throw 'Release workflow contract failed: the checkout-removal mutation fixture was not applied.'
}
$mutationCheckout = $checkoutMutation.IndexOf("        uses: actions/checkout@v4", $publishJobStart, [StringComparison]::Ordinal)
$mutationDownload = $checkoutMutation.IndexOf("        uses: actions/download-artifact@v4", $publishJobStart, [StringComparison]::Ordinal)
if ($mutationCheckout -ge 0 -and $mutationCheckout -lt $mutationDownload) {
    throw 'Release workflow contract failed: removing the publish checkout still passed the checkout assertion.'
}

if (-not (Test-Path -LiteralPath $PublisherPath -PathType Leaf)) {
    throw "Release workflow contract failed: publish script is missing: $PublisherPath"
}
$publishScript = Get-Content -Raw -LiteralPath $PublisherPath
function Assert-PublisherContract([string]$ScriptText) {
    if ($ScriptText -notmatch '(?m)dotnet\s+nuget\s+push\s+\$packagePath\s+--source\s+https://api\.nuget\.org/v3/index\.json\s+--api-key\s+\$ApiKey') {
        throw 'Release workflow contract failed: the committed publish script does not push the validated package paths.'
    }
    if ($ScriptText -notmatch '(?s)\$expected\s*=\s*@\(\s*"KeelMatrix\.KeyRingGuard\.\$Version\.nupkg"\s*"KeelMatrix\.KeyRingGuard\.\$Version\.snupkg"') {
        throw 'Release workflow contract failed: the publisher must define the exact nupkg and snupkg artifact set in deterministic order.'
    }
    if ($ScriptText -notmatch '(?m)Compare-Object\s+\(\$expected\s*\|\s*Sort-Object\)\s+\(\$actual\s*\|\s*Sort-Object\)') {
        throw 'Release workflow contract failed: the publisher must fail closed on missing or unexpected artifacts.'
    }
    if ($ScriptText -notmatch '(?m)\$LASTEXITCODE\s+-ne\s+0') {
        throw 'Release workflow contract failed: the publisher must propagate a nonzero NuGet push exit code.'
    }
}
Assert-PublisherContract $publishScript

$publisherMutationRoot = Join-Path ([IO.Path]::GetTempPath()) ('keyringguard-publisher-contract-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $publisherMutationRoot -Force | Out-Null
try {
    $mutatedPublisherPath = Join-Path $publisherMutationRoot 'Publish-ReleaseArtifacts.ps1'
    $mutatedPublisher = $publishScript.Replace(
        'dotnet nuget push $packagePath',
        'dotnet nuget push (Join-Path $PackageDirectory "unrelated-package.nupkg")')
    if ($mutatedPublisher -ceq $publishScript) {
        throw 'Release workflow contract failed: the publisher mutation fixture was not applied.'
    }
    Set-Content -LiteralPath $mutatedPublisherPath -Value $mutatedPublisher -NoNewline
    $mutationRejected = $false
    try {
        Assert-PublisherContract (Get-Content -Raw -LiteralPath $mutatedPublisherPath)
    }
    catch {
        $mutationRejected = $true
    }
    if (-not $mutationRejected) {
        throw 'Release workflow contract failed: a publisher mutation that pushes only an unrelated artifact passed the contract.'
    }
}
finally {
    Remove-Item -LiteralPath $publisherMutationRoot -Recurse -Force -ErrorAction SilentlyContinue
}

if (-not (Test-Path -LiteralPath $ArtifactSetPath -PathType Leaf)) {
    throw "Release workflow contract failed: artifact-set script is missing: $ArtifactSetPath"
}
$artifactSetScript = Get-Content -Raw -LiteralPath $ArtifactSetPath
if ($artifactSetScript -notmatch '(?s)\$expected\s*=\s*@\(\s*"KeelMatrix\.KeyRingGuard\.\$Version\.nupkg"\s*"KeelMatrix\.KeyRingGuard\.\$Version\.snupkg"') {
    throw 'Release workflow contract failed: the artifact-set script must define exactly the nupkg and snupkg names.'
}
if ($artifactSetScript -notmatch '(?m)Compare-Object\s+\$expected\s+\$actual') {
    throw 'Release workflow contract failed: the artifact-set script must fail closed on missing or unexpected artifacts.'
}

$artifactContractRoot = Join-Path ([IO.Path]::GetTempPath()) ('keyringguard-artifact-contract-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $artifactContractRoot -Force | Out-Null
try {
    function New-ArtifactFixture([string]$Name, [string[]]$Names) {
        $directory = Join-Path $artifactContractRoot $Name
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        foreach ($name in $Names) {
            New-Item -ItemType File -Path (Join-Path $directory $name) -Force | Out-Null
        }
        return $directory
    }
    function Invoke-ArtifactSet([string]$Directory) {
        $null = @(Invoke-NestedPwsh -NoProfile -File $ArtifactSetPath -Version 0.1.0 -PackageDirectory $Directory 2>&1)
        return $LASTEXITCODE
    }
    $expectedArtifactNames = @(
        'KeelMatrix.KeyRingGuard.0.1.0.nupkg'
        'KeelMatrix.KeyRingGuard.0.1.0.snupkg'
    )
    if ((Invoke-ArtifactSet (New-ArtifactFixture 'valid' $expectedArtifactNames)) -ne 0) {
        throw 'Release workflow contract failed: the unmodified artifact-set script rejected the exact two-file set.'
    }
    if ((Invoke-ArtifactSet (New-ArtifactFixture 'missing' @($expectedArtifactNames[0]))) -eq 0) {
        throw 'Release workflow contract failed: the artifact-set script accepted a missing symbol artifact.'
    }
    if ((Invoke-ArtifactSet (New-ArtifactFixture 'unexpected' ($expectedArtifactNames + 'unrelated-package.nupkg'))) -eq 0) {
        throw 'Release workflow contract failed: the artifact-set script accepted an unexpected artifact.'
    }
}
finally {
    Remove-Item -LiteralPath $artifactContractRoot -Recurse -Force -ErrorAction SilentlyContinue
}

$onBlock = [regex]::Match($text, ('(?ms)^on:' + $whitespace + '*(?<body>.*?)(?=^permissions:|' + $endOfString + ')')).Groups['body'].Value
if ([string]::IsNullOrWhiteSpace($onBlock)) { throw 'Release workflow contract failed: the workflow must declare an on block.' }
if ($onBlock -notmatch ('(?m)^push:' + $whitespace + '*$')) { throw 'Release workflow contract failed: the workflow must use a push trigger for releases.' }
if ($onBlock -notmatch ("(?m)^" + $whitespace + "+- 'v" + $literalStar + "'" + $whitespace + "*$")) { throw 'Release workflow contract failed: the workflow must trigger only for v* tags.' }
if ($onBlock -match ('(?m)^(?!push:' + $whitespace + '*$)[A-Za-z_][A-Za-z0-9_-]*:' + $whitespace + '*$')) { throw 'Release workflow contract failed: only the tag push trigger is allowed.' }
if ($onBlock -match ('(?m)^' + $whitespace + '{4}(?:branches|branches-ignore|paths|paths-ignore):')) { throw 'Release workflow contract failed: branch/path filters are not allowed on the release trigger.' }

Write-Output 'Release workflow contract: PASS (tag-only trigger, fail-closed validation, exact artifacts, and OIDC publication path).'
exit 0
