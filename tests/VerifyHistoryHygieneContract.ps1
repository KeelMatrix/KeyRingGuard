[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
)

$ErrorActionPreference = 'Stop'
$hygieneScript = Join-Path $RepositoryRoot 'scripts' 'Test-HistoryHygiene.ps1'
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('keyringguard-history-contract-' + [Guid]::NewGuid().ToString('N'))

function New-Fixture([string]$Name, [string]$Message) {
    $fixture = Join-Path $scratch $Name
    New-Item -ItemType Directory -Force -Path $fixture | Out-Null
    Set-Content -LiteralPath (Join-Path $fixture 'README.md') -Value '# Fixture' -NoNewline
    git -C $fixture init --quiet
    git -C $fixture branch -M main
    git -C $fixture config user.name KeelMatrix
    git -C $fixture config user.email noreply@keelmatrix.dev
    git -C $fixture add README.md
    git -C $fixture commit --quiet -m $Message
    return $fixture
}

function Add-Commit([string]$Fixture, [string]$Message, [string]$Branch = 'main', [string]$Author = 'KeelMatrix') {
    git -C $Fixture switch --create $Branch --quiet
    Set-Content -LiteralPath (Join-Path $Fixture "$Branch.txt") -Value $Branch -NoNewline
    git -C $Fixture add .
    git -C $Fixture -c user.name=$Author -c user.email=noreply@example.invalid commit --quiet -m $Message
}

function Restore-Main([string]$Fixture) {
    git -C $Fixture switch main --quiet
}

function Add-TagOnlyCommit([string]$Fixture, [string]$Message) {
    Add-Commit $Fixture $Message 'tag-only'
    git -C $Fixture tag tag-only-commit
    Restore-Main $Fixture
    git -C $Fixture branch --delete --force tag-only --quiet
}

function Add-MergeCommit([string]$Fixture, [string]$Message) {
    Add-Commit $Fixture 'Clean side branch' 'merge-side'
    Restore-Main $Fixture
    git -C $Fixture merge --no-ff --quiet merge-side -m $Message
}

function Invoke-Hygiene([string]$Fixture) {
    $output = @(& pwsh -NoProfile -File $hygieneScript -RepositoryRoot $Fixture 2>&1)
    [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = ($output -join [Environment]::NewLine).Trim()
    }
}

New-Item -ItemType Directory -Force -Path $scratch | Out-Null
try {
    $clean = New-Fixture 'clean' 'Keep history clean'
    $result = Invoke-Hygiene $clean
    if ($result.ExitCode -ne 0) { throw "A clean history should pass history hygiene: $($result.Output)" }

    $legitimateTrailer = ('Co-Authored' + '-By: KeelMatrix <noreply@keelmatrix.dev>')
    $cleanWithCompanyTrailer = New-Fixture 'clean-company-trailer' (@('Keep history clean', '', $legitimateTrailer) -join [Environment]::NewLine)
    $result = Invoke-Hygiene $cleanWithCompanyTrailer
    if ($result.ExitCode -ne 0) { throw "A legitimate company co-author trailer should pass history hygiene: $($result.Output)" }

    $fixtures = @(
        @{ Name = 'spacing-variant'; Message = @('Fixture history', '', ('Co-Authored' + ' - ' + 'By : Paperclip <noreply@example.invalid>')) -join [Environment]::NewLine }
        @{ Name = 'signed-off'; Message = @('Fixture history', '', ('Signed-off-by: Paperclip <noreply@example.invalid>')) -join [Environment]::NewLine }
        @{ Name = 'reviewed-by'; Message = @('Fixture history', '', ('Reviewed-by: Paperclip <noreply@example.invalid>')) -join [Environment]::NewLine }
        @{ Name = 'co-author'; Message = @('Fixture history', '', 'co_author: KeelMatrix') -join [Environment]::NewLine }
        @{ Name = 'subject-only'; Message = 'Paperclip in the subject' }
        @{ Name = 'bad-trailer'; Message = @('Fixture history', '', ('Co-Authored' + '-By: NotKeelMatrix <noreply@example.invalid>')) -join [Environment]::NewLine; SideBranch = $true }
        @{ Name = 'tag-only'; Message = @('Fixture history', '', ('Reviewed-by: NotKeelMatrix <noreply@example.invalid>')) -join [Environment]::NewLine; TagOnly = $true }
        @{ Name = 'merge-commit'; Message = @('Merge side branch', '', ('Signed-off-by: NotKeelMatrix <noreply@example.invalid>')) -join [Environment]::NewLine; Merge = $true }
        @{ Name = 'non-company-author'; Message = 'Clean side branch'; NonCompanyAuthor = $true }
    )

    foreach ($fixtureSpec in $fixtures) {
        $fixture = New-Fixture $fixtureSpec.Name $fixtureSpec.Message
        if ($fixtureSpec.SideBranch) {
            Add-Commit $fixture $fixtureSpec.Message 'side-branch'
            Restore-Main $fixture
        }
        if ($fixtureSpec.TagOnly) {
            Add-TagOnlyCommit $fixture $fixtureSpec.Message
        }
        if ($fixtureSpec.Merge) {
            Add-MergeCommit $fixture $fixtureSpec.Message
        }
        if ($fixtureSpec.NonCompanyAuthor) {
            Add-Commit $fixture $fixtureSpec.Message 'author-side' 'External Contributor'
            Restore-Main $fixture
        }

        $result = Invoke-Hygiene $fixture
        if ($result.ExitCode -eq 0) { throw "$($fixtureSpec.Name) fixture must fail history hygiene." }
    }

    Write-Output 'History hygiene contract: PASS (clean, company-attributed, trailer, subject, side-branch, tag-only and merge fixtures behave as required).'
    exit 0
}
finally {
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
