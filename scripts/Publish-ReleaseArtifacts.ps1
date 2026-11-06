[CmdletBinding()]
param(
    [string]$Version = $env:RELEASE_VERSION,
    [string]$PackageDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..' 'artifacts' 'packages')),
    [string]$ApiKey = $env:NUGET_API_KEY,
    [switch]$DryRun,
    # Test seam: the endpoint is constructed from loopback and accepts only a synthetic credential.
    [ValidateRange(0, 65535)][int]$FixturePort = 0
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Version)) { throw 'The release version is missing from RELEASE_VERSION.' }
if (-not (Test-Path -LiteralPath $PackageDirectory -PathType Container)) {
    throw "Release artifact directory '$PackageDirectory' does not exist."
}

$expected = @(
    "KeelMatrix.KeyRingGuard.$Version.nupkg"
    "KeelMatrix.KeyRingGuard.$Version.snupkg"
)
$actual = @(Get-ChildItem -LiteralPath $PackageDirectory -File | Select-Object -ExpandProperty Name)
if ((Compare-Object ($expected | Sort-Object) ($actual | Sort-Object)) -ne $null) {
    throw "Unexpected release artifact set. Expected: $($expected -join ', '). Actual: $($actual -join ', ')."
}

$packagePaths = @($expected | ForEach-Object { Join-Path $PackageDirectory $_ })
foreach ($packagePath in $packagePaths) {
    if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
        throw "Expected release artifact '$packagePath' does not exist."
    }
}

$source = 'https://api.nuget.org/v3/index.json'
$config = Join-Path $PSScriptRoot '..' 'NuGet.config'
$extra = @()
$timeoutSeconds = 90
if ($FixturePort -ne 0) {
    if ($ApiKey -cne 'local-fixture-only') { throw 'The loopback fixture requires its synthetic credential.' }
    $source = "http://127.0.0.1:$FixturePort/index.json"
    # The configuration is outside the validated artifact directory.
    $config = Join-Path (Split-Path -Parent $PackageDirectory) 'fixture.config'
    if (-not (Test-Path -LiteralPath $config -PathType Leaf)) { throw 'The isolated fixture configuration is missing.' }
    $extra = @('--allow-insecure-connections')
    $timeoutSeconds = 2
}
$pushCommandSuffix = "--source $source --api-key <temporary credential>"
if ($DryRun) {
    Write-Output "Dry run: dotnet nuget push $($packagePaths[0]) --no-symbols $pushCommandSuffix"
    Write-Output "Dry run: dotnet nuget push $($packagePaths[1]) $pushCommandSuffix"
    exit 0
}

if ([string]::IsNullOrWhiteSpace($ApiKey)) { throw 'The temporary NuGet credential is missing from NUGET_API_KEY.' }

. (Join-Path $PSScriptRoot 'Invoke-BoundedDotnet.ps1')
$states = @('not-attempted', 'not-attempted')
for ($index = 0; $index -lt $packagePaths.Count; $index++) {
    $arguments = @('nuget', 'push', $packagePaths[$index], '--source', $source, '--api-key', $ApiKey, '--symbol-api-key', $ApiKey,
        '--configfile', $config, '--timeout', [string]$timeoutSeconds, '--force-english-output') + $extra
    if ($index -eq 0) { $arguments += '--no-symbols' }
    try {
        Invoke-BoundedDotnet -Arguments $arguments -TimeoutMilliseconds (($timeoutSeconds + 2) * 1000) -IsolatedCredentials:($FixturePort -ne 0)
        $states[$index] = 'submission-confirmed'
    }
    catch {
        $states[$index] = 'failed-acceptance-uncertain'
        Write-Output "Submission state: primary=$($states[0]); symbols=$($states[1]). No rollback or indexing is implied."
        # A failed/lost response can follow server acceptance. Never replay either stage.
        exit 1
    }
}

Write-Output "Submission state: primary=$($states[0]); symbols=$($states[1]). Indexing and availability are not verified."
