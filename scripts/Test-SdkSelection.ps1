[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$required = (Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..' 'global.json') | ConvertFrom-Json).sdk.version
$selected = (& dotnet --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $selected -cne $required) { throw "Required SDK $required was not selected." }
Write-Output "SDK selection: $selected"
