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
                $_ -is [System.Management.Automation.Language.ParenExpressionAst] -or
                ($_ -is [System.Management.Automation.Language.StringConstantExpressionAst] -and $_.Value -notmatch '^-')
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

function Resolve-WorkflowScriptPath([string]$RawPath, [string]$Description) {
    $path = $RawPath.Trim()
    if ($path.Length -ge 2 -and
        (($path[0] -eq '"' -and $path[$path.Length - 1] -eq '"') -or
         ($path[0] -eq "'" -and $path[$path.Length - 1] -eq "'"))) {
        $path = $path.Substring(1, $path.Length - 2)
    }

    if ([string]::IsNullOrWhiteSpace($path) -or $path -match '[`$(){}+;,]') {
        Fail "$Description uses a non-literal -File path '$RawPath'."
    }

    $normalizedPath = $path.Replace('/', [IO.Path]::DirectorySeparatorChar).Replace([char]92, [IO.Path]::DirectorySeparatorChar)
    if ([IO.Path]::IsPathRooted($normalizedPath)) {
        Fail "$Description must use a repository-relative literal -File path, not '$RawPath'."
    }
    if ([IO.Path]::GetExtension($normalizedPath) -ine '.ps1') {
        Fail "$Description must invoke a .ps1 file, not '$RawPath'."
    }

    $resolvedPath = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot $normalizedPath))
    $rootPath = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd([char[]]@([char]92, [char]47))
    $rootPrefix = $rootPath + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        Fail "$Description resolves outside the repository: '$RawPath'."
    }
    return $resolvedPath
}

if (-not (Test-Path -LiteralPath $WorkflowPath -PathType Leaf)) { Fail "Release workflow '$WorkflowPath' does not exist." }
if (-not (Test-Path -LiteralPath $RepositoryRoot -PathType Container)) { Fail "Repository root '$RepositoryRoot' does not exist." }

$workflowText = Get-Content -Raw -LiteralPath $WorkflowPath
$workflowLines = [regex]::Split($workflowText, "`r?`n")
$scriptRoots = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$commandPattern = '(?i)(?<![A-Za-z0-9_.-])(?<command>pwsh(?:[.]exe)?|powershell(?:[.]exe)?)(?![A-Za-z0-9_.-])(?<arguments>.*)$'
$filePattern = '(?i)(?:^|\s)-File(?:\s*=\s*|\s+)(?<path>"[^"]*"|''[^'']*''|[^\s]+)'
$currentStepName = '<unnamed release step>'
$activeRunIndent = -1

for ($lineIndex = 0; $lineIndex -lt $workflowLines.Count; $lineIndex++) {
    $line = $workflowLines[$lineIndex]
    if ($line -match '^\s*-\s+name:\s*(?<name>.+?)\s*$') {
        $currentStepName = $Matches.name.Trim()
    }

    $shellMatch = [regex]::Match($line, '(?i)^\s*shell\s*:\s*(?<shell>pwsh(?:[.]exe)?|powershell(?:[.]exe)?)\s*$')
    if ($shellMatch.Success) {
        Fail "step '$currentStepName' at line $($lineIndex + 1) uses inline PowerShell through shell '$($shellMatch.Groups['shell'].Value)'. Invoke a committed .ps1 with a literal -File path instead."
    }

    $indent = ($line.Length - $line.TrimStart().Length)
    $isRunLine = $false
    if ($activeRunIndent -ge 0) {
        if ($line.Trim().Length -eq 0 -or $indent -gt $activeRunIndent) {
            $isRunLine = $true
        }
        else {
            $activeRunIndent = -1
        }
    }

    $runMatch = [regex]::Match($line, '^(?<indent>\s*)run\s*:\s*(?<value>.*)$')
    if ($runMatch.Success) {
        $isRunLine = $true
        $runValue = $runMatch.Groups['value'].Value.Trim()
        $activeRunIndent = if ($runValue -match '^[|>]') { $indent } else { -1 }
    }
    if (-not $isRunLine) { continue }

    foreach ($commandMatch in @([regex]::Matches($line, $commandPattern))) {
        $arguments = $commandMatch.Groups['arguments'].Value
        $fileMatches = @([regex]::Matches($arguments, $filePattern))
        if ($arguments -notmatch '(?i)-File' -or $fileMatches.Count -eq 0) {
            Fail "step '$currentStepName' at line $($lineIndex + 1) invokes PowerShell without a literal -File path."
        }

        foreach ($fileMatch in $fileMatches) {
            $rawPath = $fileMatch.Groups['path'].Value
            [void]$scriptRoots.Add((Resolve-WorkflowScriptPath $rawPath "step '$currentStepName' at line $($lineIndex + 1)"))
        }
    }
}

if ($scriptRoots.Count -eq 0) { Fail 'the release workflow does not invoke any PowerShell scripts through a literal -File path.' }

$pending = [System.Collections.Generic.Queue[string]]::new()
foreach ($scriptRoot in $scriptRoots) { $pending.Enqueue($scriptRoot) }
$discovered = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$checkedFiles = [System.Collections.Generic.List[object]]::new()

while ($pending.Count -gt 0) {
    $scriptPath = [IO.Path]::GetFullPath($pending.Dequeue())
    if (-not $discovered.Add($scriptPath)) { continue }
    if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) {
        Fail "release workflow closure script '$([IO.Path]::GetRelativePath($RepositoryRoot, $scriptPath))' does not exist."
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

Write-Output "Release PowerShell portability contract: PASS ($($checkedFiles.Count) scripts in the fully file-backed release-workflow closure contain no U+005C characters; empty allowlist). This proves release-workflow PowerShell is rooted in literal -File paths and closed through static dot-sources, Import-Module paths, and using module paths. It does not prove runtime construction from character codes such as [char]92 inside a checked script."
exit 0
