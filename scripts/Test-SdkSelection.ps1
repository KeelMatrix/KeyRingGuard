[CmdletBinding()]
param([switch]$RequireExact)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../build/SdkSelection.ps1')
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sdk = (Get-Content -Raw -LiteralPath (Join-Path $root 'global.json') | ConvertFrom-Json).sdk
Push-Location $root
try {
    $selected = (& dotnet --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "Unable to select SDK $($sdk.version). Install it or a permitted servicing patch; see docs/releasing.md." }
    Assert-SdkSelection $sdk $selected -RequireExact:$RequireExact
}
finally { Pop-Location }
Write-Output "SDK selection: $selected (baseline $($sdk.version); exact=$([bool]$RequireExact))"
