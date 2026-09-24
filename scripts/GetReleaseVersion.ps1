[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Tag
)

$ErrorActionPreference = 'Stop'
$component = '(?:0|[1-9][0-9]{0,3}|[1-5][0-9]{4}|6[0-4][0-9]{3}|65[0-4][0-9]{2}|655[0-2][0-9]|6553[0-4])'
if ($Tag -notmatch "^v(?<major>$component)[.](?<minor>$component)[.](?<patch>$component)$") {
    throw "Malformed release tag '$Tag'. Expected vX.Y.Z with no leading zero components and each component between 0 and 65534."
}

Write-Output "$($Matches.major).$($Matches.minor).$($Matches.patch)"
