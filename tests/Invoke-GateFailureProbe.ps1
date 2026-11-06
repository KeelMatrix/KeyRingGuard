[CmdletBinding()]
param([string]$CommandFile, [string]$ArgumentsPath, [string]$MarkerPath)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../build/Invoke-NestedPwsh.ps1')
. (Join-Path $PSScriptRoot '../build/Invoke-GateStep.ps1')
$nativeArguments = @(Get-Content -Raw -LiteralPath $ArgumentsPath | ConvertFrom-Json)
Invoke-GateStep 'Required validation' { Invoke-NestedPwsh -NoProfile -File $CommandFile @nativeArguments }
Invoke-GateStep 'Following operation' { [IO.File]::WriteAllText($MarkerPath, 'must not run') }
