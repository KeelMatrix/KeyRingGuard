[CmdletBinding()]
param(
    [string]$RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')),
    [string[]]$WorkflowPath
)

$ErrorActionPreference = 'Stop'

function Fail([string]$Message) {
    throw "Release script portability contract failed: $Message"
}

if (-not (Test-Path -LiteralPath $RepositoryRoot -PathType Container)) {
    Fail "Repository root '$RepositoryRoot' does not exist."
}
$RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd([char[]]@([char]92, [char]47))
$repositoryPrefix = $RepositoryRoot + [IO.Path]::DirectorySeparatorChar

function Get-RelativePath([string]$Path) {
    return [IO.Path]::GetRelativePath($RepositoryRoot, $Path).Replace([char]92, '/')
}

function Assert-ContainedPath([string]$Path, [string]$Description) {
    $resolvedPath = [IO.Path]::GetFullPath($Path)
    if (-not $resolvedPath.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        Fail "$Description resolves outside the repository: '$Path'."
    }
    return $resolvedPath
}

function Assert-NoReparsePoint([string]$Path, [string]$Description) {
    $currentPath = [IO.Path]::GetFullPath($Path)
    while ($true) {
        $item = Get-Item -LiteralPath $currentPath -Force -ErrorAction Stop
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            Fail "$Description uses a symlink or reparse-point path '$([IO.Path]::GetRelativePath($RepositoryRoot, $currentPath))'."
        }

        if ($currentPath.TrimEnd([char[]]@([char]92, [char]47)) -ieq $RepositoryRoot) { break }
        $parentPath = Split-Path -Parent $currentPath
        if ([string]::IsNullOrWhiteSpace($parentPath) -or $parentPath -ieq $currentPath) {
            Fail "$Description has an unresolved parent path."
        }
        $currentPath = $parentPath
    }
}

function Remove-YamlComment([string]$Value) {
    $inSingle = $false
    $inDouble = $false
    $escaped = $false
    for ($index = 0; $index -lt $Value.Length; $index++) {
        $character = $Value[$index]
        if ($inDouble -and $escaped) {
            $escaped = $false
            continue
        }
        if ($inDouble -and $character -eq [char]92) {
            $escaped = $true
            continue
        }
        if (-not $inDouble -and $character -eq "'") {
            if ($inSingle -and $index + 1 -lt $Value.Length -and $Value[$index + 1] -eq "'") {
                $index++
                continue
            }
            $inSingle = -not $inSingle
            continue
        }
        if (-not $inSingle -and $character -eq '"') {
            $inDouble = -not $inDouble
            continue
        }
        if (-not $inSingle -and -not $inDouble -and $character -eq '#' -and
            ($index -eq 0 -or [char]::IsWhiteSpace($Value[$index - 1]))) {
            return $Value.Substring(0, $index).TrimEnd()
        }
    }
    if ($inSingle -or $inDouble) { Fail "YAML scalar '$Value' has an unterminated quote." }
    return $Value.TrimEnd()
}

function Convert-YamlScalar([string]$RawValue, [string]$Description) {
    $value = (Remove-YamlComment $RawValue).Trim()
    if ([string]::IsNullOrWhiteSpace($value)) { Fail "$Description has an empty shell value." }
    if ($value.StartsWith('"')) {
        if (-not $value.EndsWith('"')) { Fail "$Description has an ambiguous quoted shell value '$RawValue'." }
        try {
            return (ConvertFrom-Json -InputObject $value -ErrorAction Stop).ToString()
        }
        catch {
            Fail "$Description has an invalid quoted shell value '$RawValue'."
        }
    }
    if ($value.StartsWith("'")) {
        if (-not $value.EndsWith("'")) { Fail "$Description has an ambiguous quoted shell value '$RawValue'." }
        return $value.Substring(1, $value.Length - 2).Replace("''", "'")
    }
    if ($value.Contains('"') -or $value.Contains("'")) {
        Fail "$Description has an ambiguous shell value '$RawValue'."
    }
    return $value
}

function Assert-ShellIsNotPowerShell([string]$RawValue, [string]$Description) {
    $shell = (Convert-YamlScalar $RawValue $Description).Trim()
    if ($shell -match '(?i)^(?:pwsh|powershell)(?:\.exe)?(?:\s|$)') {
        Fail "$Description uses inline PowerShell shell '$RawValue'. Invoke a committed .ps1 with a literal -File path instead."
    }

    $shellName = ($shell -split '\s+', 2)[0].Trim('"', "'").ToLowerInvariant()
    $knownNonPowerShellShells = @(
        'bash', 'sh', 'dash', 'zsh', 'fish', 'cmd', 'cmd.exe',
        'python', 'python3', 'node', 'ruby', 'perl'
    )
    if ($shellName -notin $knownNonPowerShellShells) {
        Fail "$Description has a shell value that cannot be classified as non-PowerShell: '$RawValue'."
    }
}

function Test-TagPushWorkflow([string]$Text) {
    $lines = [regex]::Split($Text, "`r?`n")
    $onIndent = -1
    $pushIndent = -1
    for ($index = 0; $index -lt $lines.Count; $index++) {
        $line = $lines[$index]
        if ($line.Trim().Length -eq 0 -or $line.TrimStart().StartsWith('#')) { continue }
        $indent = $line.Length - $line.TrimStart().Length
        $content = Remove-YamlComment $line.Trim()
        if ($onIndent -lt 0) {
            if ($indent -eq 0 -and $content -match '^(?:on|"on"|''on'')\s*:') { $onIndent = $indent }
            continue
        }
        if ($indent -le $onIndent) { break }
        if ($content -match '^push\s*:\s*(?<value>.*)$') {
            $pushIndent = $indent
            if ($Matches.value -match '(?i)tags\s*:') { return $true }
            continue
        }
        if ($pushIndent -ge 0 -and $indent -gt $pushIndent -and $content -match '^tags\s*:') {
            return $true
        }
        if ($pushIndent -ge 0 -and $indent -le $pushIndent) { $pushIndent = -1 }
    }
    return $false
}

function Get-WorkflowFiles() {
    $workflowDirectory = Join-Path $RepositoryRoot '.github' 'workflows'
    if (-not (Test-Path -LiteralPath $workflowDirectory -PathType Container)) {
        Fail "Workflow directory '$workflowDirectory' does not exist."
    }
    $files = @(Get-ChildItem -LiteralPath $workflowDirectory -File -Force | Where-Object { $_.Extension -in @('.yml', '.yaml') } | Sort-Object FullName)
    if ($files.Count -eq 0) { Fail 'No workflow YAML files were found.' }
    return @($files | ForEach-Object { [IO.Path]::GetFullPath($_.FullName) })
}

function Resolve-WorkflowPaths([string[]]$RequestedPaths) {
    $allWorkflows = @(Get-WorkflowFiles)
    $tagWorkflows = @($allWorkflows | Where-Object { Test-TagPushWorkflow (Get-Content -Raw -LiteralPath $_) })
    if ($tagWorkflows.Count -eq 0) { Fail 'No tag-triggered workflow YAML files were found.' }

    if ($null -eq $RequestedPaths -or $RequestedPaths.Count -eq 0) {
        return $tagWorkflows
    }

    $requested = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($rawPath in $RequestedPaths) {
        $path = [IO.Path]::GetFullPath($rawPath)
        Assert-ContainedPath $path 'Workflow path' | Out-Null
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { Fail "Workflow '$rawPath' does not exist." }
        [void]$requested.Add($path)
    }
    $unvalidated = @($tagWorkflows | Where-Object { -not $requested.Contains($_) })
    if ($unvalidated.Count -gt 0) {
        $names = $unvalidated | ForEach-Object { Get-RelativePath $_ }
        Fail "tag-triggered workflow files were not validated: $($names -join ', ')."
    }
    return @($requested | Sort-Object)
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

function Resolve-ScriptLiteralPath([string]$RawPath, [string]$ScriptPath, [string]$Description) {
    $path = $RawPath.Trim()
    if ($path.Length -ge 2 -and
        (($path[0] -eq '"' -and $path[$path.Length - 1] -eq '"') -or
         ($path[0] -eq "'" -and $path[$path.Length - 1] -eq "'"))) {
        $path = $path.Substring(1, $path.Length - 2)
    }
    if ([string]::IsNullOrWhiteSpace($path) -or $path -match '[`$(){}+;,\*\?]') {
        Fail "$Description uses a non-literal -File path '$RawPath'."
    }
    $normalizedPath = $path.Replace('/', [IO.Path]::DirectorySeparatorChar).Replace([char]92, [IO.Path]::DirectorySeparatorChar)
    if ([IO.Path]::IsPathRooted($normalizedPath)) {
        Fail "$Description must use a script-relative literal -File path, not '$RawPath'."
    }
    if ([IO.Path]::GetExtension($normalizedPath) -ine '.ps1') {
        Fail "$Description must invoke a .ps1 file, not '$RawPath'."
    }
    return Assert-ContainedPath ([IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $ScriptPath) $normalizedPath))) $Description
}

function Get-NestedPowerShellScriptPaths(
    [System.Management.Automation.Language.Ast]$Ast,
    [string]$ScriptPath
) {
    $nested = [System.Collections.Generic.List[string]]::new()
    foreach ($command in @($Ast.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.CommandAst]
            }, $true))) {
        $commandName = $command.GetCommandName()
        if ($commandName -notmatch '^(?i:pwsh|powershell)(?:\.exe)?$') { continue }

        $fileParameters = @($command.CommandElements | Where-Object {
                $_ -is [System.Management.Automation.Language.CommandParameterAst] -and $_.ParameterName -ieq 'File'
            })
        if ($fileParameters.Count -ne 1) {
            Fail "$(Get-RelativePath $ScriptPath) launches nested PowerShell without exactly one literal -File target."
        }
        $fileParameter = $fileParameters[0]
        $argument = $fileParameter.Argument
        if ($null -eq $argument) {
            $parameterIndex = [array]::IndexOf([array]$command.CommandElements, $fileParameter)
            if ($parameterIndex + 1 -ge $command.CommandElements.Count) {
                Fail "$(Get-RelativePath $ScriptPath) has a nested PowerShell -File parameter without a target."
            }
            $argument = $command.CommandElements[$parameterIndex + 1]
        }
        if ($argument -isnot [System.Management.Automation.Language.StringConstantExpressionAst]) {
            Fail "$(Get-RelativePath $ScriptPath) has a non-literal nested PowerShell -File target '$($argument.Extent.Text)'."
        }
        [void]$nested.Add((Resolve-ScriptLiteralPath $argument.Value $ScriptPath "$(Get-RelativePath $ScriptPath) nested PowerShell launch"))
    }
    return $nested.ToArray()
}

function Resolve-WorkflowScriptPath([string]$RawPath, [string]$Description) {
    $path = $RawPath.Trim()
    if ($path.Length -ge 2 -and
        (($path[0] -eq '"' -and $path[$path.Length - 1] -eq '"') -or
         ($path[0] -eq "'" -and $path[$path.Length - 1] -eq "'"))) {
        $path = $path.Substring(1, $path.Length - 2)
    }

    if ([string]::IsNullOrWhiteSpace($path) -or $path -match '[`$(){}+;,\*\?]') {
        Fail "$Description uses a non-literal -File path '$RawPath'."
    }

    $normalizedPath = $path.Replace('/', [IO.Path]::DirectorySeparatorChar).Replace([char]92, [IO.Path]::DirectorySeparatorChar)
    if ([IO.Path]::IsPathRooted($normalizedPath)) {
        Fail "$Description must use a repository-relative literal -File path, not '$RawPath'."
    }
    if ([IO.Path]::GetExtension($normalizedPath) -ine '.ps1') {
        Fail "$Description must invoke a .ps1 file, not '$RawPath'."
    }

    return Assert-ContainedPath ([IO.Path]::GetFullPath((Join-Path $RepositoryRoot $normalizedPath))) $Description
}

$workflowPaths = @(Resolve-WorkflowPaths $WorkflowPath)
$scriptRoots = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$commandPattern = '(?i)(?<![A-Za-z0-9_.-])(?:pwsh(?:[.]exe)?|powershell(?:[.]exe)?)(?![A-Za-z0-9_.-])(?<arguments>.*)$'
$filePattern = '(?i)(?:^|[\s;&|])-[\s]*File(?:\s*=\s*|\s+)(?<path>"[^"]*"|''[^'']*''|[^\s;&|]+)'

foreach ($workflowPath in $workflowPaths) {
    $workflowText = Get-Content -Raw -LiteralPath $workflowPath
    $workflowLines = [regex]::Split($workflowText, "`r?`n")
    $currentStepName = '<unnamed release step>'
    $activeRunIndent = -1
    for ($lineIndex = 0; $lineIndex -lt $workflowLines.Count; $lineIndex++) {
        $line = $workflowLines[$lineIndex]
        $indent = $line.Length - $line.TrimStart().Length
        if ($line -match '^\s*-\s+name:\s*(?<name>.+?)\s*$') {
            $currentStepName = (Remove-YamlComment $Matches.name).Trim()
        }

        if ($line -match '^\s*shell\s*:\s*(?<shell>.*)$') {
            Assert-ShellIsNotPowerShell $Matches.shell "workflow '$(Get-RelativePath $workflowPath)' step '$currentStepName' at line $($lineIndex + 1)"
        }

        $runLine = $null
        if ($activeRunIndent -ge 0) {
            if ($line.Trim().Length -eq 0 -or $indent -gt $activeRunIndent) {
                $runLine = $line.Trim()
            }
            else {
                $activeRunIndent = -1
            }
        }

        $runMatch = [regex]::Match($line, '^(?<indent>\s*)run\s*:\s*(?<value>.*)$')
        if ($runMatch.Success) {
            $rawRunValue = $runMatch.Groups['value'].Value.Trim()
            if ($rawRunValue -match '^[|>]') {
                $activeRunIndent = $runMatch.Groups['indent'].Value.Length
                $runLine = $null
            }
            else {
                $runLine = Remove-YamlComment $rawRunValue
                $activeRunIndent = -1
            }
        }
        if ([string]::IsNullOrWhiteSpace($runLine)) { continue }

        foreach ($commandMatch in @([regex]::Matches($runLine, $commandPattern))) {
            $arguments = $commandMatch.Groups['arguments'].Value
            $fileMatches = @([regex]::Matches($arguments, $filePattern))
            if ($arguments -notmatch '(?i)-\s*File' -or $fileMatches.Count -eq 0) {
                Fail "workflow '$(Get-RelativePath $workflowPath)' step '$currentStepName' at line $($lineIndex + 1) invokes PowerShell without a literal -File path."
            }
            foreach ($fileMatch in $fileMatches) {
                $rawPath = $fileMatch.Groups['path'].Value
                [void]$scriptRoots.Add((Resolve-WorkflowScriptPath $rawPath "workflow '$(Get-RelativePath $workflowPath)' step '$currentStepName' at line $($lineIndex + 1)"))
            }
        }
    }
}

if ($scriptRoots.Count -eq 0) { Fail 'the tag-triggered release workflows do not invoke any PowerShell scripts through a literal -File path.' }

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
    Assert-NoReparsePoint $scriptPath 'Release workflow closure script'

    $scriptText = Get-Content -Raw -LiteralPath $scriptPath
    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseInput($scriptText, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) { Fail "$(Get-RelativePath $scriptPath) has PowerShell parse errors." }
    [void]$checkedFiles.Add([pscustomobject]@{ Path = $scriptPath; Text = $scriptText })

    foreach ($importedPath in @(Get-ImportedScriptPaths $ast $scriptPath)) {
        $extension = [IO.Path]::GetExtension($importedPath)
        if ($extension -notin @('.ps1', '.psm1', '.psd1')) { continue }
        Assert-ContainedPath $importedPath "$(Get-RelativePath $scriptPath) static import" | Out-Null
        if (-not (Test-Path -LiteralPath $importedPath -PathType Leaf)) {
            Fail "$(Get-RelativePath $scriptPath) imports missing file '$(Get-RelativePath $importedPath)'."
        }
        $pending.Enqueue($importedPath)
    }

    foreach ($nestedPath in @(Get-NestedPowerShellScriptPaths $ast $scriptPath)) {
        if (-not (Test-Path -LiteralPath $nestedPath -PathType Leaf)) {
            Fail "$(Get-RelativePath $scriptPath) launches missing nested script '$(Get-RelativePath $nestedPath)'."
        }
        $pending.Enqueue($nestedPath)
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

Write-Output "Release PowerShell portability contract: PASS ($($checkedFiles.Count) scripts in the recognized, YAML-validated static set contain no U+005C characters. The set is closed through literal workflow -File paths, static dot-sources/Import-Module/using-module paths, and literal nested PowerShell -File paths, with repository containment and no reparse-point checked scripts. This proves only those recognized YAML/PowerShell static paths; it does not prove complete PowerShell reachability or runtime-generated commands, paths, scripts, workflow expressions, dynamic imports, reflection, native launches, shell indirection, or files outside the recognized set.)"
exit 0
