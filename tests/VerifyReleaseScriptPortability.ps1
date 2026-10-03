[CmdletBinding()]
param(
    [string]$RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')),
    [string]$WorkflowPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..' '.github' 'workflows' 'release.yml'))
)

$ErrorActionPreference = 'Stop'

function Fail([string]$Message) {
    throw "Release script portability contract failed: $Message"
}

function Get-RelativePath([string]$Path) {
    return [IO.Path]::GetRelativePath($RepositoryRoot, $Path).Replace([char]92, '/')
}

function Resolve-StaticPathExpression(
    [System.Management.Automation.Language.Ast]$Expression,
    [string]$ScriptDirectory,
    [string]$Description
) {
    if ($Expression -is [System.Management.Automation.Language.StringConstantExpressionAst]) {
        return [IO.Path]::GetFullPath((Join-Path $ScriptDirectory $Expression.Value))
    }

    if ($Expression -is [System.Management.Automation.Language.ParenExpressionAst]) {
        $pipeline = $Expression.Pipeline
        $commands = @($pipeline.PipelineElements | Where-Object {
                $_ -is [System.Management.Automation.Language.CommandAst]
            })
        if ($commands.Count -ne 1) { Fail "$Description uses a dynamic import path." }
        $command = $commands[0]
        if ($command.GetCommandName() -cne 'Join-Path') { Fail "$Description uses an unsupported import expression." }
        $elements = @($command.CommandElements)
        if ($elements.Count -lt 3 -or
            $elements[1] -isnot [System.Management.Automation.Language.VariableExpressionAst] -or
            $elements[1].VariablePath.UserPath -cne 'PSScriptRoot') {
            Fail "$Description must derive its import path from PSScriptRoot."
        }

        $resolved = $ScriptDirectory
        foreach ($element in $elements[2..($elements.Count - 1)]) {
            if ($element -isnot [System.Management.Automation.Language.StringConstantExpressionAst]) {
                Fail "$Description uses a dynamic import path."
            }
            $resolved = Join-Path $resolved $element.Value
        }
        return [IO.Path]::GetFullPath($resolved)
    }

    Fail "$Description uses an unsupported import expression."
}

function Get-ImportedScriptPaths(
    [System.Management.Automation.Language.Ast]$Ast,
    [string]$ScriptPath
) {
    $scriptDirectory = Split-Path -Parent $ScriptPath
    $imports = [System.Collections.Generic.List[string]]::new()

    foreach ($command in @($Ast.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.CommandAst]
            }, $true))) {
        if ($command.InvocationOperator -eq [System.Management.Automation.Language.TokenKind]::Dot) {
            if ($command.CommandElements.Count -ne 1) {
                Fail "$(Get-RelativePath $ScriptPath) contains a dot-source with an unsupported import expression."
            }
            [void]$imports.Add((Resolve-StaticPathExpression $command.CommandElements[0] $scriptDirectory "$(Get-RelativePath $ScriptPath) dot-source"))
            continue
        }

        if ($command.GetCommandName() -cne 'Import-Module') { continue }

        $pathExpression = @($command.CommandElements | Select-Object -Skip 1 | Where-Object {
                $_ -is [System.Management.Automation.Language.StringConstantExpressionAst] -or
                $_ -is [System.Management.Automation.Language.ParenExpressionAst]
            } | Select-Object -First 1)
        if ($pathExpression.Count -ne 1) {
            Fail "$(Get-RelativePath $ScriptPath) contains an Import-Module with a dynamic import path."
        }
        [void]$imports.Add((Resolve-StaticPathExpression $pathExpression[0] $scriptDirectory "$(Get-RelativePath $ScriptPath) Import-Module"))
    }

    foreach ($usingStatement in @($Ast.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.UsingStatementAst] -and
                    $node.UsingStatementKind -eq [System.Management.Automation.Language.UsingStatementKind]::Module
            }, $true))) {
        if ($null -eq $usingStatement.Name) {
            Fail "$(Get-RelativePath $ScriptPath) contains a using module statement without a static module path."
        }
        [void]$imports.Add((Resolve-StaticPathExpression $usingStatement.Name $scriptDirectory "$(Get-RelativePath $ScriptPath) using module"))
    }

    return $imports.ToArray()
}

if (-not (Test-Path -LiteralPath $WorkflowPath -PathType Leaf)) { Fail "Release workflow '$WorkflowPath' does not exist." }
if (-not (Test-Path -LiteralPath $RepositoryRoot -PathType Container)) { Fail "Repository root '$RepositoryRoot' does not exist." }

$workflowText = Get-Content -Raw -LiteralPath $WorkflowPath
$scriptNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($match in [regex]::Matches($workflowText, '(?i)scripts[\\/](?<name>[A-Za-z0-9_.-]+[.]ps1)')) {
    [void]$scriptNames.Add($match.Groups['name'].Value)
}
if ($scriptNames.Count -eq 0) { Fail 'the release workflow does not invoke any PowerShell scripts.' }

$pending = [System.Collections.Generic.Queue[string]]::new()
foreach ($scriptName in $scriptNames) { $pending.Enqueue((Join-Path $RepositoryRoot 'scripts' $scriptName)) }
$discovered = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$checkedFiles = [System.Collections.Generic.List[object]]::new()

while ($pending.Count -gt 0) {
    $scriptPath = [IO.Path]::GetFullPath($pending.Dequeue())
    if (-not $discovered.Add($scriptPath)) { continue }
    if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) {
        Fail "release workflow closure script '$([IO.Path]::GetFileName($scriptPath))' does not exist."
    }

    $scriptText = Get-Content -Raw -LiteralPath $scriptPath
    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseInput($scriptText, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) { Fail "$(Get-RelativePath $scriptPath) has PowerShell parse errors." }
    [void]$checkedFiles.Add([pscustomobject]@{ Path = $scriptPath; Text = $scriptText })

    foreach ($importedPath in @(Get-ImportedScriptPaths $ast $scriptPath)) {
        $extension = [IO.Path]::GetExtension($importedPath)
        if ($extension -notin @('.ps1', '.psm1', '.psd1')) { continue }
        if (-not (Test-Path -LiteralPath $importedPath -PathType Leaf)) {
            Fail "$(Get-RelativePath $scriptPath) imports missing file '$(Get-RelativePath $importedPath)'."
        }
        $pending.Enqueue($importedPath)
    }
}

if ($checkedFiles.Count -eq 0) { Fail 'the release workflow PowerShell closure is empty.' }

$windowsPathSeparator = [char]92
foreach ($entry in $checkedFiles) {
    $index = $entry.Text.IndexOf($windowsPathSeparator)
    if ($index -lt 0) { continue }
    $lineNumber = ($entry.Text.Substring(0, $index) -split "`n").Count
    Fail "$(Get-RelativePath $entry.Path) contains U+005C at line $lineNumber. The release PowerShell closure allows no Windows path-separator characters."
}

Write-Output "Release PowerShell portability contract: PASS ($($checkedFiles.Count) scripts in the release-workflow invocation/dot-source closure contain no U+005C characters; empty allowlist). This proves the checked release PowerShell source cannot contain a literal Windows path separator. It does not prove runtime construction from character codes such as [char]92 or CI-only PowerShell outside the release closure; those remain covered by the cross-platform CI matrix."
exit 0
