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
$trailerLabelPattern = '(?:Co[ \t]*-[ \t]*Authored[ \t]*-[ \t]*By|Co[ \t]*_[ \t]*Author(?:ed)?|Signed[ \t]*-[ \t]*off[ \t]*-[ \t]*by|Reviewed[ \t]*-[ \t]*by)'
$trailerPattern = '(?im)^[ \t]*(?<label>' + $trailerLabelPattern + ')[ \t]*:[ \t]*(?<identity>[^<\r\n]+?)(?:[ \t]*<[^>\r\n]*>)?[ \t]*$'

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
        if (($label -replace '[ \t]', '') -match '^co_(?:author|authored)$') {
            throw "Commit $commit (refs: $refDescription) contains unsupported attribution trailer '$label'."
        }
        if ($identity -cne 'KeelMatrix') {
            throw "Commit $commit (refs: $refDescription) contains a $label trailer for identity '$identity', not KeelMatrix."
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

Write-Output 'History and workspace hygiene: PASS (case-insensitive attribution checks cover reachable refs, subjects and bodies; split, encoded and obfuscated forms are not detected.)'
