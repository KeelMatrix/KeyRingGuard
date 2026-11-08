[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../build/Invoke-NestedPwsh.ps1')
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
& (Join-Path $root 'scripts/Test-SdkSelection.ps1')
Push-Location $root
try { $version = (& dotnet --version | Out-String).Trim() } finally { Pop-Location }
if ($LASTEXITCODE -ne 0) { throw 'The isolated host requires a validated SDK.' }
$command = Get-Command dotnet -CommandType Application | Select-Object -First 1
$hostFile = [IO.FileInfo]::new($command.Source)
$target = $hostFile.ResolveLinkTarget($true)
if ($null -ne $target) { $hostFile = [IO.FileInfo]::new($target.FullName) }
$source = $hostFile.DirectoryName
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$scratch = Join-Path $tempRoot ('keyringguard-sdk-isolation-' + [guid]::NewGuid().ToString('N'))
$links = @()
try {
    New-Item -ItemType Directory -Path (Join-Path $scratch 'sdk') -Force | Out-Null
    $isolated = Join-Path $scratch $hostFile.Name
    Copy-Item -LiteralPath $hostFile.FullName -Destination $isolated
    $fxr = Get-ChildItem -LiteralPath (Join-Path $source 'host/fxr') -Directory | Sort-Object { [version]($_.Name -split '-')[0] } -Descending | Select-Object -First 1
    $library = if ($IsWindows) { 'hostfxr.dll' } elseif ($IsMacOS) { 'libhostfxr.dylib' } else { 'libhostfxr.so' }
    & (Join-Path $PSScriptRoot 'VerifySdkPolicy.ps1') -HostFxr (Join-Path $fxr.FullName $library)
    $fxrDirectory = Join-Path $scratch "host/fxr/$($fxr.Name)"
    New-Item -ItemType Directory -Path $fxrDirectory -Force | Out-Null
    # Only the native host runtime is copied; SDK/runtime directories are linked.
    foreach ($name in @('hostfxr.dll', 'libhostfxr.so', 'libhostfxr.dylib')) {
        $path = Join-Path $fxr.FullName $name
        if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination $fxrDirectory }
    }
    Push-Location $root
    try {
        $null = @(& $isolated --version 2>&1)
        if ($LASTEXITCODE -eq 0) { throw 'An SDK-less isolated host unexpectedly selected an ambient SDK.' }
        foreach ($pair in @(@('shared', 'shared'), @("sdk/$version", "sdk/$version"))) {
            $link = Join-Path $scratch $pair[0]
            $destination = Join-Path $source $pair[1]
            $type = if ($IsWindows) { 'Junction' } else { 'SymbolicLink' }
            New-Item -ItemType $type -Path $link -Target $destination | Out-Null
            $links += $link
        }
        $selected = (& $isolated --version | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or $selected -cne $version) { throw 'SDK provisioning did not select the pinned CLI.' }
        # Exercise the real selection script and checked child runner with a
        # missing baseline and exactly one installed later servicing patch.
        $fixture = Join-Path $scratch 'repository'
        New-Item -ItemType Directory -Path (Join-Path $fixture 'scripts'), (Join-Path $fixture 'build') -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $root 'scripts/Test-SdkSelection.ps1') -Destination (Join-Path $fixture 'scripts')
        Copy-Item -LiteralPath (Join-Path $root 'build/SdkSelection.ps1') -Destination (Join-Path $fixture 'build')
        $minimum = [version]$selected
        $minimum = "$($minimum.Major).$($minimum.Minor).$($minimum.Build - 1)"
        [IO.File]::WriteAllText((Join-Path $fixture 'global.json'), (@{ sdk = @{ version = $minimum; rollForward = 'patch'; allowPrerelease = $false } } | ConvertTo-Json))
        $savedPath = $env:PATH
        try {
            $env:PATH = $scratch + [IO.Path]::PathSeparator + $savedPath
            foreach ($strict in @($false, $true)) {
                $argumentsPath = Join-Path $fixture 'arguments.json'
                $arguments = @()
                if ($strict) { $arguments = @('-RequireExact') }
                [IO.File]::WriteAllText($argumentsPath, (ConvertTo-Json -InputObject @($arguments)))
                $marker = Join-Path $fixture "following-$strict"
                $output = @(Invoke-NestedPwsh -NoProfile -File (Join-Path $PSScriptRoot 'Invoke-GateFailureProbe.ps1') -CommandFile (Join-Path $fixture 'scripts/Test-SdkSelection.ps1') -ArgumentsPath $argumentsPath -MarkerPath $marker 2>&1)
                if (($LASTEXITCODE -eq 0) -eq $strict -or (Test-Path -LiteralPath $marker) -eq $strict) { throw "SDK failure propagation mismatch: $($output -join ' ')" }
            }
        }
        finally { $env:PATH = $savedPath }
        Write-Output 'SDK orchestration: PASS (real local patch advances; strict mismatch prevents following operation).'
    }
    finally { Pop-Location }
    Write-Output "SDK isolation: PASS (absent before provisioning; selected $selected afterward)."
}
finally {
    # Remove links themselves before recursively removing the owned fixture.
    foreach ($link in $links) { [IO.Directory]::Delete($link) }
    if (-not ([IO.Path]::GetFullPath($scratch)).StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture cleanup path.' }
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
exit 0
