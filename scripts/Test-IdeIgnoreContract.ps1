[CmdletBinding()]
param([string]$IgnorePath = (Join-Path $PSScriptRoot '..' '.gitignore'))

$ErrorActionPreference = 'Stop'
$cases = @(& (Join-Path $PSScriptRoot 'Get-IdeIgnoreCases.ps1'))
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$scratch = Join-Path $tempRoot ('keyringguard-ignore-' + [guid]::NewGuid().ToString('N'))
$empty = Join-Path $scratch 'empty'
$saved = @{}
foreach ($name in @('GIT_CONFIG_GLOBAL', 'GIT_CONFIG_SYSTEM', 'GIT_CONFIG_NOSYSTEM', 'GIT_CONFIG_COUNT', 'GIT_DIR', 'GIT_WORK_TREE', 'GIT_INDEX_FILE')) {
    $saved[$name] = [Environment]::GetEnvironmentVariable($name)
    Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue
}
function Invoke-FixtureGit { param([Parameter(ValueFromRemainingArguments = $true)][string[]]$GitArguments)
    $result = @(& git -c "core.excludesFile=$empty" -c "core.hooksPath=$scratch/empty-dir" -c "init.templateDir=$scratch/empty-dir" -C $scratch @GitArguments 2>&1)
    $code = $LASTEXITCODE
    if ($code -gt 1) { throw "Git failed with exit ${code}: $($result -join ' ')" }
    [pscustomobject]@{ Code = $code; Output = $result -join [Environment]::NewLine }
}
try {
    New-Item -ItemType Directory -Path (Join-Path $scratch 'empty-dir') -Force | Out-Null
    [IO.File]::WriteAllText($empty, '')
    $env:GIT_CONFIG_GLOBAL = $empty
    $env:GIT_CONFIG_SYSTEM = $empty
    $env:GIT_CONFIG_NOSYSTEM = '1'
    $env:GIT_CONFIG_COUNT = '0'
    if ((Invoke-FixtureGit @('init', '--quiet')).Code -ne 0) { throw 'Git initialization failed.' }
    Copy-Item -LiteralPath $IgnorePath -Destination (Join-Path $scratch '.gitignore')
    foreach ($case in $cases) {
        foreach ($path in $case.Paths) {
            if ((Invoke-FixtureGit @('check-ignore', '--no-index', '--quiet', '--', $path)).Code -ne 0) { throw "Required noise is visible: $path" }
        }
    }
    $examples = @('.env.example', 'src/.env.example', 'local.example.json', 'src/local.example.json', 'fixtures/local.sample.json', 'src/local.sample.json', 'src/coverage/README.md', 'tests/coverage/baseline.xml')
    foreach ($path in $examples) {
        if ((Invoke-FixtureGit @('check-ignore', '--no-index', '--quiet', '--', $path)).Code -ne 1) { throw "Intended example is ignored: $path" }
        $physical = Join-Path $scratch $path
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($physical)) | Out-Null
        [IO.File]::WriteAllText($physical, 'example')
        if ((Invoke-FixtureGit @('add', '--', $path)).Code -ne 0) { throw 'An intended example is not addable.' }
    }
    $tracked = Join-Path $scratch '.vs/tracked.txt'
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($tracked)) | Out-Null
    [IO.File]::WriteAllText($tracked, 'tracked example')
    if ((Invoke-FixtureGit @('add', '--force', '--', '.vs/tracked.txt')).Code -ne 0) { throw 'Cannot prepare tracked example.' }
    $before = (Invoke-FixtureGit @('status', '--porcelain', '--untracked-files=all')).Output
    foreach ($path in $cases.Paths) {
        $physical = Join-Path $scratch $path
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($physical)) | Out-Null
        [IO.File]::WriteAllText($physical, 'generated noise')
    }
    if ((Invoke-FixtureGit @('status', '--porcelain', '--untracked-files=all')).Output -cne $before) { throw 'Generated noise dirtied the fixture.' }
    [IO.File]::WriteAllText($tracked, 'modified tracked example')
    $difference = Invoke-FixtureGit @('diff', '--name-only', '--', '.vs/tracked.txt')
    if ($difference.Code -ne 0 -or $difference.Output -cne '.vs/tracked.txt') { throw 'An ignore rule hid changes to a tracked example.' }
    Write-Output "IDE ignore behavior: PASS ($($cases.Count) noise classes, $($examples.Count) addable exceptions, tracked-file semantics)."
}
finally {
    foreach ($name in $saved.Keys) {
        if ($null -eq $saved[$name]) { Remove-Item -LiteralPath "Env:$name" -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
    }
    $resolved = [IO.Path]::GetFullPath($scratch)
    if (-not $resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture cleanup path.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
