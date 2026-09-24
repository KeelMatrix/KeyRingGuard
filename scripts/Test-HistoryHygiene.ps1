[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
)

$ErrorActionPreference = 'Stop'
$windowsPathSeparator = [char]92
$separatorPattern = '(?:/|' + [regex]::Escape($windowsPathSeparator) + ')'
$wordBoundary = [char]92 + 'b'
$root = (Resolve-Path $RepositoryRoot).Path
$tracked = @(git -C $root ls-files)
if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate tracked files.' }
$workflowPaths = @($tracked | Where-Object { $_ -match ('(^|' + $separatorPattern + ')[.]github' + $separatorPattern + 'workflows(' + $separatorPattern + '|$)') })
if ($workflowPaths.Count -gt 0) {
    $allowedWorkflow = '.github/workflows/release.yml'
    if ($workflowPaths.Count -ne 1 -or $workflowPaths[0].Replace($windowsPathSeparator, '/') -cne $allowedWorkflow) {
        throw 'Only the tag-only release workflow may be tracked in this repository.'
    }

    $workflowContract = Join-Path $root 'tests' 'VerifyReleaseWorkflowContract.ps1'
    $contractOutput = @(& pwsh -NoProfile -File $workflowContract -WorkflowPath (Join-Path $root '.github' 'workflows' 'release.yml') 2>&1)
    $contractExitCode = $LASTEXITCODE
    if ($contractExitCode -ne 0) {
        throw "The tracked release workflow did not pass its tag-only contract: $($contractOutput -join [Environment]::NewLine)"
    }
}

$commits = @(git -C $root log --all --format='%H' | Sort-Object -Unique)
if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate commit history.' }

$refs = @(git -C $root for-each-ref --format='%(refname)')
if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate repository refs.' }
$commitRefs = @{}
foreach ($ref in $refs) {
    $refCommits = @(git -C $root rev-list --topo-order $ref)
    if ($LASTEXITCODE -ne 0) { throw "Could not enumerate commits reachable from ref $ref." }
    foreach ($refCommit in $refCommits) {
        if (-not $commitRefs.ContainsKey($refCommit)) {
            $commitRefs[$refCommit] = [System.Collections.Generic.List[string]]::new()
        }
        $commitRefs[$refCommit].Add($ref)
    }
}

$forbiddenTerms = @(
    ('ag' + 'ent')
    ('mo' + 'del')
    ('paper' + 'clip')
)
$forbiddenAlternatives = @($forbiddenTerms | ForEach-Object { [regex]::Escape($_) })
$forbiddenHistoryPattern = '(?i)(?<![A-Za-z])(?:' + [string]::Join('|', [string[]]$forbiddenAlternatives) + ')(?![A-Za-z])'
$inlineWhitespaceCharacters = [char[]] @([char]9, [char]11, [char]12, [char]32, [char]160, [char]0x3000)
$inlineWhitespaceClass = '[' + [string]::Concat($inlineWhitespaceCharacters) + ']*'
$lineBreakCharacters = [string]::Concat([char]13, [char]10)
$trailerLabelPattern = '(?:Co' + $inlineWhitespaceClass + '-' + $inlineWhitespaceClass + 'Authored' + $inlineWhitespaceClass + '-' + $inlineWhitespaceClass + 'By|Co' + $inlineWhitespaceClass + '_' + $inlineWhitespaceClass + 'Author(?:ed)?|Signed' + $inlineWhitespaceClass + '-' + $inlineWhitespaceClass + 'off' + $inlineWhitespaceClass + '-' + $inlineWhitespaceClass + 'by|Reviewed' + $inlineWhitespaceClass + '-' + $inlineWhitespaceClass + 'by|Acked' + $inlineWhitespaceClass + '-' + $inlineWhitespaceClass + 'by|Tested' + $inlineWhitespaceClass + '-' + $inlineWhitespaceClass + 'by|Committed' + $inlineWhitespaceClass + '-' + $inlineWhitespaceClass + 'by)'
$trailerPattern = '(?im)^' + $inlineWhitespaceClass + '(?<label>' + $trailerLabelPattern + ')' + $inlineWhitespaceClass + ':' + $inlineWhitespaceClass + '(?<identity>[^<' + $lineBreakCharacters + ']+?)(?:' + $inlineWhitespaceClass + '<(?<email>[^>' + $lineBreakCharacters + ']*)>)?' + $inlineWhitespaceClass + '$'
$unambiguousAutomationIdentityTerms = @(
    ('ag' + 'ent')
    ('mo' + 'del')
    ('assis' + 'tant')
    ('b' + 'ot')
    ('paper' + 'clip')
    ('open' + 'ai')
    ('anthr' + 'opic')
    ('cla' + 'ude')
    ('gem' + 'ini')
    ('copil' + 'ot')
    'action'
    'actions'
    'ci'
    'cd'
    'robot'
    'bot'
    'automation'
    'automated'
    'noreply'
    'no-reply'
    'no_reply'
    'buildkite'
    'appveyor'
    'drone'
    'woodpecker'
    'tekton'
)
$unambiguousAutomationIdentityAlternatives = @($unambiguousAutomationIdentityTerms | ForEach-Object { [regex]::Escape($_) })
$unambiguousAutomationIdentityPattern = '(?i)(?<![A-Za-z])(?:' + [string]::Join('|', [string[]]$unambiguousAutomationIdentityAlternatives) + ')(?![A-Za-z])'
$ciProductIdentityTerms = @(
    'github actions'
    'buildkite ci'
    'azure pipelines'
    'gitlab ci'
    'circleci'
    'teamcity'
    'appveyor'
    'drone'
    'woodpecker'
    'tekton'
)
$ciProductIdentityAlternatives = @($ciProductIdentityTerms | ForEach-Object { [regex]::Escape($_) })
$ciProductIdentityPattern = '(?i)(?<![A-Za-z])(?:' + [string]::Join('|', [string[]]$ciProductIdentityAlternatives) + ')(?![A-Za-z])'
$ambiguousIdentityTerms = @(
    'travis'
    'jenkins'
    'argo'
    'build'
    'builder'
    'runner'
    'service'
    'account'
    'pipeline'
    'pipelines'
)
$ambiguousIdentityAlternatives = @($ambiguousIdentityTerms | ForEach-Object { [regex]::Escape($_) })
$ambiguousIdentityPattern = '(?i)(?<![A-Za-z])(?:' + [string]::Join('|', [string[]]$ambiguousIdentityAlternatives) + ')(?![A-Za-z])'
$machineEmailTerms = @(
    'noreply'
    'no-reply'
    'no_reply'
    'agent'
    'bot'
    'robot'
    'automation'
    'automated'
    'ci'
    'cd'
    'actions'
    'model'
    'assistant'
    'github-actions'
    'githubactions'
    'buildkite'
    'buildkite-ci'
    'azure-pipelines'
    'azurepipelines'
    'gitlab-ci'
    'gitlabci'
    'circleci'
    'teamcity'
    'appveyor'
    'drone'
    'woodpecker'
    'tekton'
    'travis-ci'
    'jenkins-ci'
    'argo-ci'
)
$machineEmailAlternatives = @($machineEmailTerms | ForEach-Object { [regex]::Escape($_) })
$machineEmailPattern = '(?i)(?:^|[@._-])(?:' + [string]::Join('|', [string[]]$machineEmailAlternatives) + ')(?=$|[@._-])'

foreach ($commit in $commits) {
    $refDescription = if ($commitRefs.ContainsKey($commit)) { $commitRefs[$commit] -join ', ' } else { '<unmapped ref>' }
    $author = (git -C $root show -s --format='%an' $commit) -join ''
    $committer = (git -C $root show -s --format='%cn' $commit) -join ''
    if ($LASTEXITCODE -ne 0) { throw "Could not inspect commit $commit (refs: $refDescription)." }
    if ($author -and $author -notin @('KeelMatrix', 'Dependabot')) {
        throw "Commit $commit (refs: $refDescription) has a non-company author '$author'."
    }
    if ($committer -and $committer -notin @('KeelMatrix', 'Dependabot')) {
        throw "Commit $commit (refs: $refDescription) has a non-company committer '$committer'."
    }

    $message = (git -C $root show -s --format='%B' $commit) -join [Environment]::NewLine
    if ($LASTEXITCODE -ne 0) { throw "Could not inspect commit $commit." }
    $message = $message.Replace(([char]13).ToString(), '')

    foreach ($trailer in [regex]::Matches($message, $trailerPattern)) {
        $label = $trailer.Groups['label'].Value
        $identity = $trailer.Groups['identity'].Value.Trim()
        $email = $trailer.Groups['email'].Value.Trim()
        $labelWithoutWhitespace = $label
        foreach ($whitespaceCharacter in $inlineWhitespaceCharacters) {
            $labelWithoutWhitespace = $labelWithoutWhitespace.Replace($whitespaceCharacter.ToString(), '')
        }
        if ($labelWithoutWhitespace -match '^co_(?:author|authored)$') {
            throw "Commit $commit (refs: $refDescription) contains unsupported attribution trailer '$label'."
        }
        if ($labelWithoutWhitespace -ieq 'Co-Authored-By' -and $identity -cne 'KeelMatrix' -and $identity -cne 'Dependabot') {
            throw "Commit $commit (refs: $refDescription) contains a $label trailer for non-company author identity '$identity'."
        }
        $isAllowedCompanyCoAuthor = $labelWithoutWhitespace -ieq 'Co-Authored-By' -and $identity -in @('KeelMatrix', 'Dependabot')
        $isUnambiguousAutomation =
            [regex]::IsMatch($identity, $unambiguousAutomationIdentityPattern) -or
            [regex]::IsMatch($email, $unambiguousAutomationIdentityPattern) -or
            [regex]::IsMatch($identity, $ciProductIdentityPattern) -or
            [regex]::IsMatch($email, $ciProductIdentityPattern)
        $hasAmbiguousAutomationSignal =
            [regex]::IsMatch($identity, $ambiguousIdentityPattern) -and
            [regex]::IsMatch($email, $machineEmailPattern)
        if (-not $isAllowedCompanyCoAuthor -and ($isUnambiguousAutomation -or $hasAmbiguousAutomationSignal)) {
            $reportedIdentity = if ($email) { "$identity <$email>" } else { $identity }
            throw "Commit $commit (refs: $refDescription) contains a $label trailer for internal automation identity '$reportedIdentity'."
        }
    }

    $forbiddenMatch = [regex]::Match($message, $forbiddenHistoryPattern)
    if ($forbiddenMatch.Success) {
        throw "Commit $commit (refs: $refDescription) contains prohibited history wording '$($forbiddenMatch.Value)'."
    }
}

foreach ($relativePath in $tracked) {
    $path = Join-Path $root $relativePath
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        $content = Get-Content -Raw -LiteralPath $path
        if ($content -match ('(?-i)' + $wordBoundary + '[A-Z]{2,5}-[0-9]+' + $wordBoundary)) {
            throw "Issue identifiers found in $relativePath."
        }
    }
}

Write-Output 'History and workspace hygiene: PASS (case-insensitive attribution checks cover reachable commit refs, subjects and bodies; unambiguous automation identities and CI product phrases are rejected directly, while ambiguous human-name identities require a machine-signaled email; form feed and vertical tab are accepted as label separators; annotated tag messages and Git notes are not scanned; split, encoded and obfuscated forms are not detected.)'
