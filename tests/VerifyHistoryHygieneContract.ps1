[CmdletBinding()]
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [switch]$IdentityOnly
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../build/Invoke-NestedPwsh.ps1')
$hygieneScript = Join-Path $RepositoryRoot 'scripts' 'Test-HistoryHygiene.ps1'
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('keyringguard-history-contract-' + [Guid]::NewGuid().ToString('N'))

function Write-FixtureMessage([string]$Message) {
    $messagePath = Join-Path $scratch 'message.txt'
    Set-Content -LiteralPath $messagePath -Value $Message -NoNewline -Encoding utf8
    return $messagePath
}

function New-Fixture([string]$Name, [string]$Message) {
    $fixture = Join-Path $scratch $Name
    New-Item -ItemType Directory -Force -Path $fixture | Out-Null
    Set-Content -LiteralPath (Join-Path $fixture 'README.md') -Value '# Fixture' -NoNewline
    git -C $fixture init --quiet
    git -C $fixture branch -M main
    git -C $fixture config user.name KeelMatrix
    git -C $fixture config user.email noreply@keelmatrix.dev
    git -C $fixture add README.md
    git -C $fixture commit --quiet --file (Write-FixtureMessage $Message)
    return $fixture
}

function Add-Commit([string]$Fixture, [string]$Message, [string]$Branch = 'main', [string]$Author = 'KeelMatrix') {
    git -C $Fixture switch --create $Branch --quiet
    Set-Content -LiteralPath (Join-Path $Fixture "$Branch.txt") -Value $Branch -NoNewline
    git -C $Fixture add .
    git -C $Fixture -c user.name=$Author -c user.email=noreply@example.invalid commit --quiet --file (Write-FixtureMessage $Message)
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
    git -C $Fixture merge --no-ff --quiet merge-side --file (Write-FixtureMessage $Message)
}

function Invoke-Hygiene([string]$Fixture) {
    $output = @(Invoke-NestedPwsh -NoProfile -File $hygieneScript -RepositoryRoot $Fixture 2>&1)
    [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = ($output -join [Environment]::NewLine).Trim()
    }
}

# Write objects directly: commit launchers can normalize ordinary commit identities.
function Add-IdentityCommit(
    [string]$Fixture, [string]$Author, [string]$AuthorEmail,
    [string]$Committer, [string]$CommitterEmail,
    [string]$Message = 'Maintain source', [string[]]$Parents = @(),
    [string]$Ref = 'refs/heads/main'
) {
    $tree = (git -C $Fixture write-tree) -join ''
    if ($LASTEXITCODE -ne 0) { throw 'Could not write fixture tree.' }
    $lines = @("tree $tree")
    $lines += @($Parents | ForEach-Object { "parent $_" })
    $lines += @(
        "author $Author <$AuthorEmail> 1700000000 +0000"
        "committer $Committer <$CommitterEmail> 1700000000 +0000"
        ''
        $Message
        ''
    )
    $objectPath = Join-Path $scratch 'commit-object.txt'
    [IO.File]::WriteAllText($objectPath, ($lines -join "`n"), [Text.UTF8Encoding]::new($false))
    $commit = (git -C $Fixture hash-object -t commit -w -- $objectPath) -join ''
    if ($LASTEXITCODE -ne 0) { throw 'Could not write fixture commit.' }
    git -C $Fixture update-ref $Ref $commit
    if ($LASTEXITCODE -ne 0) { throw 'Could not update fixture ref.' }
    $formats = @('%an', '%ae', '%cn', '%ce', '%B')
    $values = @($Author, $AuthorEmail, $Committer, $CommitterEmail, $Message)
    for ($index = 0; $index -lt $formats.Count; $index++) {
        $stored = (git -C $Fixture show -s "--format=$($formats[$index])" $commit) -join "`n"
        if ($LASTEXITCODE -ne 0 -or $stored.TrimEnd() -cne $values[$index].TrimEnd()) {
            throw "Fixture commit did not retain $($formats[$index])."
        }
    }
    return $commit
}

function Test-CommitIdentities {
    $cases = @(
        @{ Name = 'company'; Author = 'KeelMatrix'; Committer = 'KeelMatrix'; Email = 'keelmatrix@gmail.com'; Pass = $true }
        @{ Name = 'web'; Author = 'KeelMatrix'; Committer = 'GitHub'; Email = 'noreply@github.com'; Pass = $true }
        @{ Name = 'dependency'; Author = 'Dependabot'; Committer = 'Dependabot'; Email = 'dependabot@users.noreply.github.com'; Pass = $true }
        @{ Name = 'dependency-web'; Author = 'Dependabot'; Committer = 'GitHub'; Email = 'noreply@github.com'; Pass = $true }
        @{ Name = 'unknown-author'; Author = 'Other'; Committer = 'GitHub'; Email = 'noreply@github.com'; Pass = $false }
        @{ Name = 'github-author'; Author = 'GitHub'; Committer = 'GitHub'; Email = 'noreply@github.com'; Pass = $false }
        @{ Name = 'unknown-committer'; Author = 'KeelMatrix'; Committer = 'Other'; Email = 'noreply@github.com'; Pass = $false }
        @{ Name = 'web-wrong-email'; Author = 'KeelMatrix'; Committer = 'GitHub'; Email = 'other@github.com'; Pass = $false }
        @{ Name = 'web-wrong-domain'; Author = 'KeelMatrix'; Committer = 'GitHub'; Email = 'noreply@github.com.invalid'; Pass = $false }
        @{ Name = 'web-wrong-name'; Author = 'KeelMatrix'; Committer = 'GitHub Web'; Email = 'noreply@github.com'; Pass = $false }
        @{ Name = 'web-name-case'; Author = 'KeelMatrix'; Committer = 'github'; Email = 'noreply@github.com'; Pass = $false }
        @{ Name = 'web-email-case'; Author = 'KeelMatrix'; Committer = 'GitHub'; Email = 'NOREPLY@github.com'; Pass = $false }
        @{ Name = 'web-trailer'; Author = 'KeelMatrix'; Committer = 'GitHub'; Email = 'noreply@github.com'; Pass = $false; Message = "Maintain source`n`nReviewed-by: GitHub Actions" }
    )
    foreach ($case in $cases) {
        $fixture = New-Fixture ('identity-' + $case.Name) 'Maintain source'
        $message = if ($case.Message) { $case.Message } else { 'Maintain source' }
        $null = Add-IdentityCommit $fixture $case.Author 'keelmatrix@gmail.com' $case.Committer $case.Email $message
        $result = Invoke-Hygiene $fixture
        if (($result.ExitCode -eq 0) -ne $case.Pass) {
            throw "Stored identity case $($case.Name) returned $($result.ExitCode): $($result.Output)"
        }
    }

    foreach ($badAncestor in @($false, $true)) {
        $fixture = New-Fixture "identity-ancestor-$badAncestor" 'Maintain source'
        $author = if ($badAncestor) { 'Other' } else { 'KeelMatrix' }
        $ancestor = Add-IdentityCommit $fixture $author 'keelmatrix@gmail.com' 'GitHub' 'noreply@github.com'
        $null = Add-IdentityCommit $fixture 'KeelMatrix' 'keelmatrix@gmail.com' 'KeelMatrix' 'keelmatrix@gmail.com' -Parents @($ancestor)
        $result = Invoke-Hygiene $fixture
        if (($result.ExitCode -eq 0) -eq $badAncestor) { throw "Non-HEAD identity case failed: $($result.Output)" }
    }
    foreach ($badMerge in @($false, $true)) {
        $fixture = New-Fixture "identity-merge-$badMerge" 'Maintain source'
        $base = (git -C $fixture rev-parse HEAD) -join ''
        $side = Add-IdentityCommit $fixture 'KeelMatrix' 'keelmatrix@gmail.com' 'KeelMatrix' 'keelmatrix@gmail.com' -Message 'Side change' -Parents @($base) -Ref 'refs/heads/side'
        $email = if ($badMerge) { 'other@github.com' } else { 'noreply@github.com' }
        $null = Add-IdentityCommit $fixture 'KeelMatrix' 'keelmatrix@gmail.com' 'GitHub' $email -Message 'Merge source' -Parents @($base, $side)
        $result = Invoke-Hygiene $fixture
        if (($result.ExitCode -eq 0) -eq $badMerge) { throw "Merge identity case failed: $($result.Output)" }
    }
    Write-Output 'Stored commit identities: PASS (13 role/address/trailer cases, non-HEAD and merge positives/negatives).'
}

New-Item -ItemType Directory -Force -Path $scratch | Out-Null
try {
    Test-CommitIdentities
    if ($IdentityOnly) { exit 0 }
    $clean = New-Fixture 'clean' 'Keep history clean'
    git -C $clean tag --annotate --message 'Clean release tag' clean-release HEAD
    git -C $clean notes --ref=contract add --message 'Clean release note' HEAD
    $result = Invoke-Hygiene $clean
    if ($result.ExitCode -ne 0) { throw "A clean history should pass history hygiene: $($result.Output)" }

    $legitimateTrailers = @(
        'Co-Authored' + '-By: KeelMatrix'
        'Co-Authored' + '-By: KeelMatrix <noreply@keelmatrix.dev>'
    )
    $cleanWithCompanyTrailer = New-Fixture 'clean-company-trailer' (@('Keep history clean', '') + $legitimateTrailers -join [Environment]::NewLine)
    $result = Invoke-Hygiene $cleanWithCompanyTrailer
    if ($result.ExitCode -ne 0) { throw "A legitimate company co-author trailer should pass history hygiene: $($result.Output)" }

    $prohibitedInternalTrailer = 'Co-Authored' + '-By: ' + ('Paper' + 'clip') + ' <noreply@' + ('paper' + 'clip') + '.ing>'
    $cleanWithProhibitedInternalTrailer = New-Fixture 'clean-prohibited-internal-trailer' (@('Keep history clean', '') + $prohibitedInternalTrailer -join [Environment]::NewLine)
    $result = Invoke-Hygiene $cleanWithProhibitedInternalTrailer
    if ($result.ExitCode -eq 0) { throw "An internal co-author trailer must fail history hygiene." }

    $humanTrailers = @(
        'Signed-off-by: External Contributor'
        'Reviewed-by: Human Maintainer'
        'Signed-off-by: Jane Doe <jane@example.org>'
        'Acked-by: Release Verifier <verifier@example.invalid>'
        'Tested-by: Test Maintainer <tests@example.invalid>'
        'Committed-by: Release Maintainer <release@example.invalid>'
        'Signed-off-by: Robotics <robotics@example.org>'
        'Signed-off-by: Travis <travis@example.org>'
        'Signed-off-by: Jenkins <jenkins@example.org>'
        'Signed-off-by: Argo <argo@example.org>'
        'Signed-off-by: Build <build@example.org>'
        'Signed-off-by: Builder <builder@example.org>'
        'Signed-off-by: Runner <runner@example.org>'
        'Signed-off-by: Jane Builder <jane.builder@example.org>'
        'Signed-off-by: Pat Service <pat.service@example.org>'
        'Signed-off-by: Account <account@example.org>'
        'Signed-off-by: Pipeline <pipeline@example.org>'
        'Signed-off-by: Service <service@example.org>'
    )
    $cleanWithHumanTrailers = New-Fixture 'clean-human-trailers' (@('Keep history clean', '') + $humanTrailers -join [Environment]::NewLine)
    $result = Invoke-Hygiene $cleanWithHumanTrailers
    if ($result.ExitCode -ne 0) { throw "Ordinary human attribution trailers should pass history hygiene: $($result.Output)" }

    $automationIdentity = ('paper' + 'clip')
    $automationBotIdentity = 'Build ' + ('b' + 'ot')
    $fullWidth = [char]0x3000
    $nonBreakingSpace = [char]160
    $tab = [char]9
    $formFeed = [char]12
    $verticalTab = [char]11
    $separatorTrailers = @(
        ('Signed' + $formFeed + '-' + $formFeed + 'off' + $formFeed + '-' + $formFeed + 'by: External Contributor')
        ('Reviewed' + $verticalTab + '-' + $verticalTab + 'by: Human Maintainer')
    )
    $separatorFixture = New-Fixture 'additional-separators' (@('Fixture history', '') + $separatorTrailers -join [Environment]::NewLine)
    $result = Invoke-Hygiene $separatorFixture
    if ($result.ExitCode -ne 0) { throw "Form feed and vertical tab label separators should be recognized: $($result.Output)" }

    $fixtures = @(
        @{ Name = 'spacing-variant'; Message = @('Fixture history', '', ('Co' + $tab + ' - ' + 'Authored' + $fullWidth + '-' + 'By: ' + $automationIdentity + ' <noreply@example.invalid>')) -join [Environment]::NewLine }
        @{ Name = 'multiple-spaces'; Message = @('Fixture history', '', ('Co  -  Authored  -  By: ' + $automationIdentity + ' <noreply@example.invalid>')) -join [Environment]::NewLine }
        @{ Name = 'signed-off'; Message = @('Fixture history', '', ('Signed' + $nonBreakingSpace + '-' + 'off' + $fullWidth + '-' + 'by: ' + $automationBotIdentity + ' <noreply@example.invalid>')) -join [Environment]::NewLine }
        @{ Name = 'reviewed-by'; Message = @('Fixture history', '', ('Reviewed-by: ' + $automationIdentity + ' <noreply@example.invalid>')) -join [Environment]::NewLine }
        @{ Name = 'acked-by'; Message = @('Fixture history', '', ('Acked-by: ' + $automationBotIdentity + ' <noreply@example.invalid>')) -join [Environment]::NewLine }
        @{ Name = 'tested-by'; Message = @('Fixture history', '', ('Tested-by: ' + $automationBotIdentity + ' <noreply@example.invalid>')) -join [Environment]::NewLine }
        @{ Name = 'committed-by'; Message = @('Fixture history', '', ('Committed-by: ' + $automationBotIdentity + ' <noreply@example.invalid>')) -join [Environment]::NewLine }
        @{ Name = 'github-actions'; Message = @('Fixture history', '', 'Signed-off-by: GitHub Actions') -join [Environment]::NewLine }
        @{ Name = 'buildkite-ci'; Message = @('Fixture history', '', 'Reviewed-by: Buildkite CI') -join [Environment]::NewLine }
        @{ Name = 'azure-pipelines'; Message = @('Fixture history', '', 'Tested-by: Azure Pipelines') -join [Environment]::NewLine }
        @{ Name = 'ci-automation'; Message = @('Fixture history', '', 'Committed-by: CI Automation') -join [Environment]::NewLine }
        @{ Name = 'teamcity'; Message = @('Fixture history', '', 'Signed-off-by: TeamCity') -join [Environment]::NewLine }
        @{ Name = 'circleci'; Message = @('Fixture history', '', 'Reviewed-by: CircleCI') -join [Environment]::NewLine }
        @{ Name = 'gitlab-ci'; Message = @('Fixture history', '', 'Acked-by: GitLab CI') -join [Environment]::NewLine }
        @{ Name = 'appveyor'; Message = @('Fixture history', '', 'Committed-by: AppVeyor') -join [Environment]::NewLine }
        @{ Name = 'drone'; Message = @('Fixture history', '', 'Signed-off-by: Drone') -join [Environment]::NewLine }
        @{ Name = 'woodpecker'; Message = @('Fixture history', '', 'Reviewed-by: Woodpecker') -join [Environment]::NewLine }
        @{ Name = 'tekton'; Message = @('Fixture history', '', 'Tested-by: Tekton') -join [Environment]::NewLine }
        @{ Name = 'address-only-noreply'; Message = @('Fixture history', '', 'Signed-off-by: Human Maintainer <noreply@example.invalid>') -join [Environment]::NewLine }
        @{ Name = 'address-only-vendor'; Message = @('Fixture history', '', 'Signed-off-by: Human Maintainer <openai@example.invalid>') -join [Environment]::NewLine }
        @{ Name = 'unambiguous-bot'; Message = @('Fixture history', '', 'Signed-off-by: bot <person@example.org>') -join [Environment]::NewLine }
        @{ Name = 'unambiguous-robot'; Message = @('Fixture history', '', 'Signed-off-by: robot <person@example.org>') -join [Environment]::NewLine }
        @{ Name = 'unambiguous-automation'; Message = @('Fixture history', '', 'Signed-off-by: automation <person@example.org>') -join [Environment]::NewLine }
        @{ Name = 'unambiguous-automated'; Message = @('Fixture history', '', 'Signed-off-by: automated <person@example.org>') -join [Environment]::NewLine }
        @{ Name = 'unambiguous-ci'; Message = @('Fixture history', '', 'Signed-off-by: ci <person@example.org>') -join [Environment]::NewLine }
        @{ Name = 'unambiguous-cd'; Message = @('Fixture history', '', 'Signed-off-by: cd <person@example.org>') -join [Environment]::NewLine }
        @{ Name = 'unambiguous-actions'; Message = @('Fixture history', '', 'Signed-off-by: actions <person@example.org>') -join [Environment]::NewLine }
        @{ Name = 'unambiguous-agent'; Message = @('Fixture history', '', 'Signed-off-by: agent <person@example.org>') -join [Environment]::NewLine }
        @{ Name = 'unambiguous-model'; Message = @('Fixture history', '', 'Signed-off-by: model <person@example.org>') -join [Environment]::NewLine }
        @{ Name = 'unambiguous-assistant'; Message = @('Fixture history', '', 'Signed-off-by: assistant <person@example.org>') -join [Environment]::NewLine }
        @{ Name = 'unambiguous-noreply'; Message = @('Fixture history', '', 'Signed-off-by: noreply <person@example.org>') -join [Environment]::NewLine }
        @{ Name = 'unambiguous-no-reply'; Message = @('Fixture history', '', 'Signed-off-by: no-reply <person@example.org>') -join [Environment]::NewLine }
        @{ Name = 'unambiguous-no-reply-underscore'; Message = @('Fixture history', '', 'Signed-off-by: no_reply <person@example.org>') -join [Environment]::NewLine }
        @{ Name = 'github-actions'; Message = @('Fixture history', '', 'Signed-off-by: GitHub Actions <person@example.org>') -join [Environment]::NewLine }
        @{ Name = 'co-author'; Message = @('Fixture history', '', 'co_author: KeelMatrix') -join [Environment]::NewLine }
        @{ Name = 'subject-only'; Message = $automationIdentity + ' in the subject' }
        @{ Name = 'internal-process-wording'; Message = ('Cover ' + 'internal ' + 'task IDs in history checks') }
        @{ Name = 'bad-trailer'; Message = @('Fixture history', '', ('Co-Authored' + '-By: NotKeelMatrix <noreply@example.invalid>')) -join [Environment]::NewLine; SideBranch = $true }
        @{ Name = 'internal-wrong-email'; Message = @('Fixture history', '', ('Co-Authored' + '-By: ' + ('Paper' + 'clip') + ' <paperclip@example.invalid>')) -join [Environment]::NewLine; SideBranch = $true }
        @{ Name = 'merge-commit'; Message = @('Merge side branch', '', ('Signed-off-by: ' + $automationBotIdentity + ' <noreply@example.invalid>')) -join [Environment]::NewLine; Merge = $true }
        @{ Name = 'non-company-author'; Message = 'Clean side branch'; NonCompanyAuthor = $true }
        @{ Name = 'ambiguous-travis-machine-email'; Message = @('Fixture history', '', 'Signed-off-by: Travis <noreply@travis-ci.org>') -join [Environment]::NewLine }
        @{ Name = 'ambiguous-jenkins-machine-email'; Message = @('Fixture history', '', 'Signed-off-by: Jenkins <jenkins@users.noreply.github.com>') -join [Environment]::NewLine }
        @{ Name = 'ambiguous-argo-machine-email'; Message = @('Fixture history', '', 'Signed-off-by: Argo <argo@ci.example.org>') -join [Environment]::NewLine }
        @{ Name = 'ambiguous-build-machine-email'; Message = @('Fixture history', '', 'Signed-off-by: Build <build@buildkite.example.org>') -join [Environment]::NewLine }
        @{ Name = 'ambiguous-builder-machine-email'; Message = @('Fixture history', '', 'Signed-off-by: Builder <builder@buildkite.example.org>') -join [Environment]::NewLine }
        @{ Name = 'ambiguous-runner-machine-email'; Message = @('Fixture history', '', 'Signed-off-by: Runner <runner@github-actions.example.org>') -join [Environment]::NewLine }
        @{ Name = 'ambiguous-jane-builder-machine-email'; Message = @('Fixture history', '', 'Signed-off-by: Jane Builder <jane.builder@noreply.example.org>') -join [Environment]::NewLine }
        @{ Name = 'ambiguous-pat-service-machine-email'; Message = @('Fixture history', '', 'Signed-off-by: Pat Service <pat.service@ci.example.org>') -join [Environment]::NewLine }
        @{ Name = 'ambiguous-account-machine-email'; Message = @('Fixture history', '', 'Signed-off-by: Account <account@users.noreply.github.com>') -join [Environment]::NewLine }
        @{ Name = 'ambiguous-pipeline-machine-email'; Message = @('Fixture history', '', 'Signed-off-by: Pipeline <pipeline@ci.example.org>') -join [Environment]::NewLine }
        @{ Name = 'ambiguous-service-machine-email'; Message = @('Fixture history', '', 'Signed-off-by: Service <service@automation.example.org>') -join [Environment]::NewLine }
        @{ Name = 'internal-task-id'; Message = ('Fix ' + 'KEE-' + '1234 behavior') }
        @{ Name = 'annotated-tag-message'; Message = ('Release ' + 'KEE-' + '1234'); AnnotatedTag = $true }
        @{ Name = 'tag-name'; Message = 'Clean fixture history'; TagName = ('release-' + 'KEE-' + '1234') }
        @{ Name = 'note-content'; Message = ('Note for ' + 'KEE-' + '1234'); Note = $true }
        @{ Name = 'ref-name'; Message = 'Clean fixture history'; RefName = ('kee-' + '1234-side') }
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
            $null = Add-IdentityCommit $fixture 'External Contributor' 'person@example.org' 'KeelMatrix' 'keelmatrix@gmail.com' -Message $fixtureSpec.Message -Ref 'refs/heads/author-side'
        }
        if ($fixtureSpec.AnnotatedTag) {
            git -C $fixture tag --annotate --message $fixtureSpec.Message annotated-fixture HEAD
        }
        if ($fixtureSpec.TagName) {
            git -C $fixture tag $fixtureSpec.TagName HEAD
        }
        if ($fixtureSpec.Note) {
            git -C $fixture notes --ref=contract add --message $fixtureSpec.Message HEAD
        }
        if ($fixtureSpec.RefName) {
            Add-Commit $fixture $fixtureSpec.Message $fixtureSpec.RefName
            Restore-Main $fixture
        }

        $result = Invoke-Hygiene $fixture
        if ($result.ExitCode -eq 0) { throw "$($fixtureSpec.Name) fixture must fail history hygiene." }
    }

    Write-Output 'History hygiene contract: PASS (clean, company-attributed, human, CI, task-ID, address-only, separator, subject, side-branch, tag, note and merge fixtures behave as required).'
    exit 0
}
finally {
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
