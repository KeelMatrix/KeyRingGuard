[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$tracked = @(git -C $root ls-files)
if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate tracked files.' }
if ($tracked -match '(^|[\\/])\.github[\\/]workflows([\\/]|$)') { throw 'Private repository workflow files are not allowed for this product round.' }

$authors = @(git -C $root log --format='%an%n%cn' -n 50)
if ($authors | Where-Object { $_ -and $_ -notin @('KeelMatrix', 'Dependabot') }) {
    throw 'History contains a non-company author or committer.'
}

foreach ($relativePath in $tracked) {
    $path = Join-Path $root $relativePath
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        $content = Get-Content -Raw -LiteralPath $path
        if ($content -match '(?i)\b[A-Z]{2,5}-\d+\b') {
            throw "Issue identifiers found in $relativePath."
        }
    }
}

Write-Output 'History and workspace hygiene: PASS'
