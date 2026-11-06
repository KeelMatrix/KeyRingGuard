[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$version = (Get-Content -Raw -LiteralPath (Join-Path $root 'global.json') | ConvertFrom-Json).sdk.version
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
