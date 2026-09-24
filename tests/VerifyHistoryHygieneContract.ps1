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
    git -C $fixture config user.name KeelMatrix
    git -C $fixture config user.email noreply@keelmatrix.dev
    git -C $fixture add README.md
    git -C $fixture commit --quiet -m $Message
    return $fixture
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
    $forbiddenTrailer = ('Co-Authored' + '-By: ') + 'NotKeelMatrix <noreply@example.invalid>'
    $badMessage = @('Fixture history', '', $forbiddenTrailer) -join [Environment]::NewLine
    $bad = New-Fixture 'bad-trailer' $badMessage
    $result = Invoke-Hygiene $bad
    if ($result.ExitCode -eq 0) { throw 'A non-company co-author trailer must fail history hygiene.' }

    $clean = New-Fixture 'clean' 'Keep history clean'
    $result = Invoke-Hygiene $clean
    if ($result.ExitCode -ne 0) { throw "A clean history should pass history hygiene: $($result.Output)" }

    Write-Output 'History hygiene contract: PASS (non-company co-author trailers fail and clean history passes).'
    exit 0
}
finally {
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
