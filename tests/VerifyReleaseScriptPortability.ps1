[CmdletBinding()]
param(
    [string]$RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')),
    [string]$WorkflowPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..' '.github' 'workflows' 'release.yml'))
)

$ErrorActionPreference = 'Stop'

function Fail([string]$Message) {
    throw "Release script portability contract failed: $Message"
}

if (-not (Test-Path -LiteralPath $WorkflowPath -PathType Leaf)) { Fail "Release workflow '$WorkflowPath' does not exist." }

$workflowText = Get-Content -Raw -LiteralPath $WorkflowPath
$scriptNames = [System.Collections.Generic.Queue[string]]::new()
$discovered = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

foreach ($match in [regex]::Matches($workflowText, '(?i)scripts[\\/](?<name>[A-Za-z0-9_.-]+\.ps1)')) {
    $scriptNames.Enqueue($match.Groups['name'].Value)
}

if ($scriptNames.Count -eq 0) { Fail 'the release workflow does not invoke any PowerShell scripts.' }

while ($scriptNames.Count -gt 0) {
    $scriptName = $scriptNames.Dequeue()
    if (-not $discovered.Add($scriptName)) { continue }

    $scriptPath = Join-Path $RepositoryRoot 'scripts' $scriptName
    if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) { Fail "release script '$scriptName' does not exist at '$scriptPath'." }

    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) { Fail "release script '$scriptName' has PowerShell parse errors." }

    foreach ($parameter in $ast.ParamBlock.Parameters) {
        if ($null -eq $parameter.DefaultValue) { continue }
        $defaultText = $parameter.DefaultValue.Extent.Text
        if ($defaultText -match '\\') {
            Fail "$scriptName parameter '$($parameter.Name.VariablePath.UserPath)' contains a Windows-only backslash in its default expression: $defaultText"
        }

        $joinPath = $parameter.DefaultValue.Find({
            param($node)
            $node -is [System.Management.Automation.Language.CommandAst] -and
                $node.GetCommandName() -eq 'Join-Path'
        }, $true)
        if ($null -eq $joinPath) { continue }

        $elements = @($joinPath.CommandElements)
        if ($elements.Count -lt 3 -or $elements[1] -isnot [System.Management.Automation.Language.VariableExpressionAst] -or
            $elements[1].VariablePath.UserPath -cne 'PSScriptRoot') {
            continue
        }

        $resolved = Join-Path $RepositoryRoot 'scripts'
        foreach ($element in $elements[2..($elements.Count - 1)]) {
            if ($element -isnot [System.Management.Automation.Language.StringConstantExpressionAst]) {
                $resolved = $null
                break
            }
            $resolved = Join-Path $resolved $element.Value
        }
        if ($null -eq $resolved) { continue }

        $resolved = [IO.Path]::GetFullPath($resolved)
        $pathType = if ($parameter.Name.VariablePath.UserPath -ceq 'RepositoryRoot') { 'Container' } else { 'Leaf' }
        if (-not (Test-Path -LiteralPath $resolved -PathType $pathType)) {
            Fail "$scriptName parameter '$($parameter.Name.VariablePath.UserPath)' does not resolve to a $pathType on this host: $resolved"
        }
    }

    foreach ($match in [regex]::Matches((Get-Content -Raw -LiteralPath $scriptPath), '(?i)(?<name>[A-Za-z0-9_.-]+\.ps1)')) {
        $helper = $match.Groups['name'].Value
        if (Test-Path -LiteralPath (Join-Path $RepositoryRoot 'scripts' $helper) -PathType Leaf) {
            $scriptNames.Enqueue($helper)
        }
    }
}

Write-Output "Release script portability contract: PASS ($($discovered.Count) workflow/helper scripts; default paths resolve on this host)."
