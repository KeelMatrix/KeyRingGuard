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

$authors = @(git -C $root log --format='%an%n%cn')
if ($authors | Where-Object { $_ -and $_ -notin @('KeelMatrix', 'Dependabot') }) {
    throw 'History contains a non-company author or committer.'
}

$commits = @(git -C $root log --format='%H')
if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate commit history.' }

$forbiddenTerms = @('ag' + 'ent', 'mo' + 'del', 'paper' + 'clip')
$forbiddenHistoryPattern = '(?i)(?<![A-Za-z])(?:' + (($forbiddenTerms | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')(?![A-Za-z])'
$trailerLabel = 'Co-Authored' + '-By:'
$trailerPattern = '(?im)^[ ' + $windowsPathSeparator + 't]*' + $trailerLabel + '[ ' + $windowsPathSeparator + 't]*(?<identity>[^<' + $windowsPathSeparator + 'r' + $windowsPathSeparator + 'n]+?)(?:[ ' + $windowsPathSeparator + 't]*<[^>' + $windowsPathSeparator + 'r' + $windowsPathSeparator + 'n]*>)?[ ' + $windowsPathSeparator + 't]*$'

foreach ($commit in $commits) {
    $message = (git -C $root show -s --format='%s%n%b' $commit) -join [Environment]::NewLine
    if ($LASTEXITCODE -ne 0) { throw "Could not inspect commit $commit." }
    $message = $message.Replace(([char]13).ToString(), '')

    foreach ($trailer in [regex]::Matches($message, $trailerPattern)) {
        if ($trailer.Groups['identity'].Value.Trim() -cne 'KeelMatrix') {
            throw "Commit $commit contains a co-author trailer for an identity other than KeelMatrix."
        }
    }

    if ($message -match $forbiddenHistoryPattern) {
        throw "Commit $commit contains prohibited attribution wording."
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

Write-Output 'History and workspace hygiene: PASS'
