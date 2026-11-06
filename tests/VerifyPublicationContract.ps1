[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../build/Invoke-NestedPwsh.ps1')
Add-Type -Path (Join-Path $PSScriptRoot 'LoopbackPublicationFeed.cs')
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publisher = Join-Path $root 'scripts/Publish-ReleaseArtifacts.ps1'
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$scratch = Join-Path $tempRoot ('keyringguard-publication-' + [guid]::NewGuid().ToString('N'))
$artifacts = Join-Path $scratch 'packages with spaces'
New-Item -ItemType Directory -Path $artifacts | Out-Null
[IO.File]::WriteAllText((Join-Path $scratch 'fixture.config'), '<configuration><packageSources><clear /></packageSources></configuration>')
$feed = [KeyRingGuard.PublicationFixture.LoopbackFeed]::new()
function Invoke-Publisher([string]$Path = $publisher, [string]$Directory = $artifacts) {
    $output = @(Invoke-NestedPwsh -NoProfile -File $Path -Version 0.1.0 -PackageDirectory $Directory -ApiKey local-fixture-only -FixturePort $feed.Port 2>&1)
    [pscustomobject]@{ Code = $LASTEXITCODE; Output = $output -join [Environment]::NewLine }
}
try {
    $hashes = @{}
    foreach ($extension in @('nupkg', 'snupkg')) {
        $path = Join-Path $artifacts "KeelMatrix.KeyRingGuard.0.1.0.$extension"
        $zip = [IO.Compression.ZipFile]::Open($path, [IO.Compression.ZipArchiveMode]::Create)
        try {
            $writer = [IO.StreamWriter]::new($zip.CreateEntry('KeelMatrix.KeyRingGuard.nuspec').Open())
            try { $writer.Write('<package><metadata><id>KeelMatrix.KeyRingGuard</id><version>0.1.0</version><authors>KeelMatrix</authors><description>Synthetic upload fixture.</description></metadata></package>') }
            finally { $writer.Dispose() }
            $writer = [IO.StreamWriter]::new($zip.CreateEntry("lib/net8.0/fixture.$extension").Open())
            try { $writer.Write('synthetic artifact ' + $extension) } finally { $writer.Dispose() }
        }
        finally { $zip.Dispose() }
        $hashes[$extension] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    }
    $feed.Reset(201, 201, '')
    $result = Invoke-Publisher
    if ($result.Code -ne 0) { throw "Real CLI submission failed: $($result.Output)" }
    $uploads = @($feed.Uploads.ToArray())
    if ($uploads.Count -ne 2 -or $uploads[0].Path -cne '/packages' -or $uploads[1].Path -cne '/symbols' -or
        $uploads[0].Hash -cne $hashes.nupkg -or $uploads[1].Hash -cne $hashes.snupkg) { throw 'Upload count, order, or artifact hashes differ.' }
    if ($result.Output -notmatch 'primary=submission-confirmed; symbols=submission-confirmed') { throw 'Successful submission state is missing.' }
    Write-Output "PUBLICATION_UPLOADS: primary=1 symbols=1 nupkg_sha256=$($hashes.nupkg) snupkg_sha256=$($hashes.snupkg)"
    $cases = @(
        @{ Primary = 400; Symbols = 201 }, @{ Primary = 401; Symbols = 201 }, @{ Primary = 409; Symbols = 201 },
        @{ Primary = 201; Symbols = 400 }, @{ Primary = 201; Symbols = 401 }, @{ Primary = 201; Symbols = 404 }, @{ Primary = 201; Symbols = 409 }
    )
    foreach ($case in $cases) {
        $feed.Reset($case.Primary, $case.Symbols, '')
        $result = Invoke-Publisher
        $uploads = @($feed.Uploads.ToArray())
        $primaryFailed = $case.Primary -ne 201
        $expected = if ($primaryFailed) { 'primary=failed-acceptance-uncertain; symbols=not-attempted' } else { 'primary=submission-confirmed; symbols=failed-acceptance-uncertain' }
        if ($result.Code -eq 0 -or $result.Output -notmatch [regex]::Escape($expected) -or
            @($uploads | Where-Object Path -eq '/packages').Count -ne 1 -or
            @($uploads | Where-Object Path -eq '/symbols').Count -ne [int](-not $primaryFailed)) { throw "Failure state/count mismatch: $($result.Output)" }
    }
    foreach ($fault in @('lost', 'delay')) {
        $feed.Reset(201, 201, $fault)
        $result = Invoke-Publisher
        $uploads = @($feed.Uploads.ToArray())
        if ($result.Code -eq 0 -or $result.Output -notmatch 'primary=failed-acceptance-uncertain; symbols=not-attempted' -or
            $uploads.Count -lt 1 -or @($uploads | Where-Object Path -eq '/symbols').Count -ne 0) { throw 'Unknown acceptance was misreported or advanced to symbols.' }
        Write-Output "PUBLICATION_UNKNOWN: fault=$fault wire_uploads=$($uploads.Count) script_attempts=1 symbols=0 (CLI transport retries are not script replays)"
        # Drain the intentionally delayed response before resetting the fixture.
        if ($fault -eq 'delay') { Start-Sleep -Seconds 4 }
    }
    $mutantRoot = Join-Path $scratch 'mutant'
    New-Item -ItemType Directory -Path $mutantRoot | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'scripts/Invoke-BoundedDotnet.ps1') -Destination $mutantRoot
    $text = [IO.File]::ReadAllText($publisher)
    $mutated = $text.Replace("if (`$index -eq 0) { `$arguments += '--no-symbols' }", '')
    if ($text -ceq $mutated) { throw 'The automatic-symbol mutation was not applied.' }
    $mutant = Join-Path $mutantRoot 'Publish-ReleaseArtifacts.ps1'
    [IO.File]::WriteAllText($mutant, $mutated)
    $feed.Reset(201, 201, '')
    $result = Invoke-Publisher $mutant
    if ($result.Code -eq 0 -or @($feed.Uploads.ToArray() | Where-Object Path -eq '/symbols').Count -ne 2) { throw 'Automatic plus explicit symbols was not detected by actual uploads.' }
    foreach ($invalid in @('missing', 'extra', 'wrong-version')) {
        $directory = Join-Path $scratch $invalid
        New-Item -ItemType Directory -Path $directory | Out-Null
        Copy-Item -LiteralPath (Join-Path $artifacts 'KeelMatrix.KeyRingGuard.0.1.0.nupkg') -Destination $directory
        if ($invalid -ne 'missing') { Copy-Item -LiteralPath (Join-Path $artifacts 'KeelMatrix.KeyRingGuard.0.1.0.snupkg') -Destination $directory }
        if ($invalid -eq 'extra') { [IO.File]::WriteAllText((Join-Path $directory 'extra.nupkg'), 'fixture') }
        if ($invalid -eq 'wrong-version') { Rename-Item -LiteralPath (Join-Path $directory 'KeelMatrix.KeyRingGuard.0.1.0.nupkg') -NewName 'KeelMatrix.KeyRingGuard.0.1.1.nupkg' }
        $feed.Reset(201, 201, '')
        $result = Invoke-Publisher -Directory $directory
        if ($result.Code -eq 0 -or $feed.Uploads.Count -ne 0) { throw 'Invalid artifacts reached publication.' }
    }
    if ($feed.Errors.Count -ne 0) { throw "Fixture protocol failure: $($feed.Errors.ToArray() -join ', ')" }
    Write-Output 'Publication contract: PASS (14 real CLI/count/state/artifact cases; loopback only, synthetic credential).'
}
finally {
    $feed.Dispose()
    if (-not ([IO.Path]::GetFullPath($scratch)).StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture cleanup path.' }
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
exit 0
