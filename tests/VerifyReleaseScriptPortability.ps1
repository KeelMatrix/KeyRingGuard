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

# The checked set is every PowerShell source file under the repository root, including scripts,
# modules, data files, hooks, and other source locations. Git metadata is not release source.
# This contract proves that PowerShell source text and the pack-sensitive MSBuild inputs use
# portable path separators. Regex escape sequences are not path separators and must remain
# valid in cross-platform PowerShell source. It does not prove release-job portability for
# .json, .props, NuGet.config, or separators constructed at runtime.
$windowsPathSeparator = [char]92
$gitMetadataRoot = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot '.git'))
$checkedEntries = @(
    Get-ChildItem -LiteralPath $RepositoryRoot -Recurse -Force |
        Where-Object {
            -not [IO.Path]::GetFullPath($_.FullName).StartsWith(
                $gitMetadataRoot + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase)
        }
)
$symlinkEntries = @(
    $checkedEntries | Where-Object {
        ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    }
)
if ($symlinkEntries.Count -gt 0) {
    $relativeSymlinks = @($symlinkEntries | ForEach-Object {
        [IO.Path]::GetRelativePath($RepositoryRoot, $_.FullName).Replace($windowsPathSeparator, '/')
    })
    Fail "the checked source set contains symlinked or reparse-point entries: $($relativeSymlinks -join ', '). Symlink targets are not followed; the contract fails closed."
}

$checkedPowerShellFiles = @(
    $checkedEntries |
        Where-Object {
            $_.PSIsContainer -eq $false -and
            $_.Extension -in @('.ps1', '.psm1', '.psd1')
        }
) | Sort-Object -Property FullName

if ($checkedPowerShellFiles.Count -eq 0) { Fail 'the checked PowerShell source set is empty.' }

function Add-RegexStringNodes(
    [System.Management.Automation.Language.Ast]$Expression,
    [System.Collections.Generic.HashSet[int]]$RegexStringOffsets
) {
    if ($null -eq $Expression) { return }

    if ($Expression -is [System.Management.Automation.Language.StringConstantExpressionAst] -or
        $Expression -is [System.Management.Automation.Language.ExpandableStringExpressionAst]) {
        [void]$RegexStringOffsets.Add($Expression.Extent.StartOffset)
    }

    foreach ($stringNode in @($Expression.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.StringConstantExpressionAst] -or
            $node -is [System.Management.Automation.Language.ExpandableStringExpressionAst]
    }, $true))) {
        [void]$RegexStringOffsets.Add($stringNode.Extent.StartOffset)
    }
}

function Add-RegexVariables(
    [System.Management.Automation.Language.Ast]$Expression,
    [System.Collections.Generic.HashSet[string]]$RegexVariableNames
) {
    if ($null -eq $Expression) { return }

    if ($Expression -is [System.Management.Automation.Language.VariableExpressionAst]) {
        [void]$RegexVariableNames.Add($Expression.VariablePath.UserPath)
    }

    foreach ($variableNode in @($Expression.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.VariableExpressionAst]
    }, $true))) {
        [void]$RegexVariableNames.Add($variableNode.VariablePath.UserPath)
    }
}

function Test-SimplePathCharacter([char]$Character) {
    return [char]::IsLetterOrDigit($Character) -or $Character -in @('_', '.', '-')
}

function Test-StringExpressionConstructed([System.Management.Automation.Language.Ast]$StringNode) {
    $parent = $StringNode.Parent
    while ($null -ne $parent) {
        if ($parent -is [System.Management.Automation.Language.BinaryExpressionAst]) {
            $operatorName = [string]$parent.Operator
            if ($operatorName -in @('Plus', 'Iplus', 'Cplus', 'Format', 'Iformat', 'Cformat')) {
                return $true
            }
        }

        $parent = $parent.Parent
    }

    return $false
}

function Get-StringBackslashViolation(
    [string]$Value,
    [bool]$RegexOperand,
    [bool]$Constructed,
    [char]$WindowsPathSeparator
) {
    $regexEscapeLetters = 'ABDGKPSTWZabcdefknprstuvwxz'
    $length = $Value.Length

    for ($index = 0; $index -lt $length; $index++) {
        if ($Value[$index] -ne $WindowsPathSeparator) { continue }

        $runEnd = $index
        while ($runEnd -lt $length -and $Value[$runEnd] -eq $WindowsPathSeparator) {
            $runEnd++
        }
        $runLength = $runEnd - $index

        if (-not $RegexOperand) {
            return [pscustomobject]@{ Index = $index; Reason = 'string contains a literal Windows path separator' }
        }

        if ($runLength -gt 2) {
            return [pscustomobject]@{ Index = $index; Reason = 'regex operand contains an unsupported backslash run' }
        }

        $nextIndex = $runEnd
        if ($runLength -eq 1 -and $nextIndex -lt $length) {
            $nextCharacter = $Value[$nextIndex]
            if ([char]::IsLetter($nextCharacter) -and $regexEscapeLetters.IndexOf($nextCharacter) -lt 0) {
                return [pscustomobject]@{ Index = $index; Reason = 'regex operand contains an unknown escape' }
            }
        }

        # A drive-prefixed path with one source separator is a path, not a regex escape.
        if ($runLength -eq 1 -and $index -ge 2 -and
            [char]::IsLetter($Value[$index - 2]) -and $Value[$index - 1] -eq ':') {
            return [pscustomobject]@{ Index = $index; Reason = 'drive-prefixed string contains a literal Windows path separator' }
        }

        $leftStart = $index - 1
        while ($leftStart -ge 0 -and (Test-SimplePathCharacter $Value[$leftStart])) {
            $leftStart--
        }
        $rightEnd = $runEnd
        while ($rightEnd -lt $length -and (Test-SimplePathCharacter $Value[$rightEnd])) {
            $rightEnd++
        }

        $hasLeftSegment = $leftStart -lt ($index - 1)
        $hasRightSegment = $rightEnd -gt $runEnd
        $rightStartsWithRegexPunctuation = $hasRightSegment -and $Value[$runEnd] -eq '.'
        $leftSegment = if ($hasLeftSegment) { $Value.Substring($leftStart + 1, $index - $leftStart - 1) } else { '' }
        $leftStartsWithVariable = $hasLeftSegment -and $leftStart -ge 0 -and $Value[$leftStart] -eq '$'
        $pathLikeRightSegment = $hasRightSegment -and (($rightEnd - $runEnd -gt 1) -or
            $leftStartsWithVariable -or $leftSegment -in @('.', '..'))
        if ($runLength -eq 1 -and $hasLeftSegment -and $hasRightSegment -and
            $pathLikeRightSegment -and -not $rightStartsWithRegexPunctuation) {
            return [pscustomobject]@{ Index = $index; Reason = 'string contains path-like segments separated by a Windows path separator' }
        }

        # Interpolated variables, subexpressions, and format placeholders can end in a
        # non-path character, so identify those boundaries explicitly.
        if ($runLength -eq 1 -and $index -gt 0) {
            $prefix = $Value.Substring(0, $index)
            if (($Value[$index - 1] -eq ')' -and $prefix.Contains('$(')) -or
                ($Value[$index - 1] -eq '}' -and ($prefix.Contains('${') -or
                    ($prefix.StartsWith('{') -and $prefix.EndsWith('}'))))) {
                return [pscustomobject]@{ Index = $index; Reason = 'interpolated or formatted expression contains a literal Windows path separator' }
            }
        }

        # A path fragment supplied by concatenation or format construction commonly
        # starts with the separator, which has no left segment to classify above.
        if ($Constructed -and $runLength -eq 1 -and $index -eq 0 -and
            $hasRightSegment -and $rightEnd - $runEnd -gt 1 -and $Value[$runEnd] -ne '.') {
            return [pscustomobject]@{ Index = $index; Reason = 'constructed path fragment contains a literal Windows path separator' }
        }
    }

    return $null
}

foreach ($sourceFile in $checkedPowerShellFiles) {
    $scriptText = Get-Content -Raw -LiteralPath $sourceFile.FullName
    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseInput($scriptText, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) {
        $relativePath = [IO.Path]::GetRelativePath($RepositoryRoot, $sourceFile.FullName).Replace($windowsPathSeparator, '/')
        Fail "$relativePath has PowerShell parse errors."
    }

    $regexStringOffsets = [System.Collections.Generic.HashSet[int]]::new()
    $regexVariableNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $assignments = @($ast.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.AssignmentStatementAst]
    }, $true))

    foreach ($assignment in $assignments) {
        $variable = $assignment.Left -as [System.Management.Automation.Language.VariableExpressionAst]
        if ($null -ne $variable -and
            ($variable.VariablePath.UserPath -match '(?i)(pattern|regex)' -or
                $variable.VariablePath.UserPath -in @('re', 'regex', 'pattern'))) {
            [void]$regexVariableNames.Add($variable.VariablePath.UserPath)
        }
    }

    foreach ($binaryExpression in @($ast.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.BinaryExpressionAst]
    }, $true))) {
        $operatorName = [string]$binaryExpression.Operator
        if ($operatorName -notmatch '(?i)(match|replace|split)') { continue }

        $operand = $binaryExpression.Right
        if ($operatorName -match '(?i)replace' -and
            $operand -is [System.Management.Automation.Language.ArrayLiteralAst]) {
            if ($operand.Elements.Count -eq 0) { continue }
            $operand = $operand.Elements[0]
        }

        Add-RegexStringNodes $operand $regexStringOffsets
        Add-RegexVariables $operand $regexVariableNames
    }

    foreach ($memberExpression in @($ast.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.InvokeMemberExpressionAst]
    }, $true))) {
        if (-not $memberExpression.Static -or $memberExpression.Expression -isnot [System.Management.Automation.Language.TypeExpressionAst]) {
            continue
        }

        $typeName = [string]$memberExpression.Expression.TypeName.FullName
        if ($typeName -notin @('regex', 'System.Text.RegularExpressions.Regex')) { continue }

        $memberName = [string]$memberExpression.Member.Value
        if ($memberName -in @('Match', 'Matches', 'IsMatch', 'Replace', 'Split')) {
            if ($memberExpression.Arguments.Count -gt 1) {
                Add-RegexStringNodes $memberExpression.Arguments[1] $regexStringOffsets
                Add-RegexVariables $memberExpression.Arguments[1] $regexVariableNames
            }
        }
        elseif ($memberName -eq 'New' -and $memberExpression.Arguments.Count -gt 0) {
            Add-RegexStringNodes $memberExpression.Arguments[0] $regexStringOffsets
            Add-RegexVariables $memberExpression.Arguments[0] $regexVariableNames
        }
    }

    # Resolve the small amount of assignment flow needed for pattern variables such as
    # $windowsPathPattern. This keeps regex escapes allowed without allowing arbitrary
    # strings merely because they contain a familiar escape such as \s.
    for ($iteration = 0; $iteration -lt 8; $iteration++) {
        $changed = $false
        foreach ($assignment in $assignments) {
            $variable = $assignment.Left -as [System.Management.Automation.Language.VariableExpressionAst]
            if ($null -eq $variable -or -not $regexVariableNames.Contains($variable.VariablePath.UserPath)) { continue }

            $before = $regexStringOffsets.Count
            Add-RegexStringNodes $assignment.Right $regexStringOffsets
            if ($regexStringOffsets.Count -ne $before) { $changed = $true }

            foreach ($rhsVariable in @($assignment.Right.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.VariableExpressionAst]
            }, $true))) {
                if ($regexVariableNames.Add($rhsVariable.VariablePath.UserPath)) { $changed = $true }
            }
        }

        if (-not $changed) { break }
    }

    $stringNodes = @($ast.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.StringConstantExpressionAst] -or
            $node -is [System.Management.Automation.Language.ExpandableStringExpressionAst]
    }, $true))

    foreach ($stringNode in $stringNodes) {
        $value = [string]$stringNode.Value
        if ($value.IndexOf($windowsPathSeparator) -lt 0) { continue }

        $regexOperand = $regexStringOffsets.Contains($stringNode.Extent.StartOffset)
        $constructed = Test-StringExpressionConstructed $stringNode
        $violation = Get-StringBackslashViolation $value $regexOperand $constructed $windowsPathSeparator
        if ($null -eq $violation) { continue }

        $backslashOrdinal = 0
        for ($valueIndex = 0; $valueIndex -lt $violation.Index; $valueIndex++) {
            if ($value[$valueIndex] -eq $windowsPathSeparator) { $backslashOrdinal++ }
        }

        $rawIndex = -1
        for ($extentIndex = 0; $extentIndex -lt $stringNode.Extent.Text.Length; $extentIndex++) {
            if ($stringNode.Extent.Text[$extentIndex] -ne $windowsPathSeparator) { continue }
            if ($backslashOrdinal -eq 0) {
                $rawIndex = $extentIndex
                break
            }
            $backslashOrdinal--
        }

        $relativePath = [IO.Path]::GetRelativePath($RepositoryRoot, $sourceFile.FullName).Replace($windowsPathSeparator, '/')
        $absoluteOffset = $stringNode.Extent.StartOffset + [Math]::Max($rawIndex, 0)
        $lineNumber = ($scriptText.Substring(0, $absoluteOffset) -split "`n").Count
        Fail "$relativePath contains a Windows path separator at line ${lineNumber}: $($violation.Reason). Regex escape sequences are permitted only in recognized regex operands."
    }
}

$packInputFiles = @(
    (Join-Path $RepositoryRoot 'src' 'KeelMatrix.KeyRingGuard' 'KeelMatrix.KeyRingGuard.csproj')
    (Join-Path $RepositoryRoot 'Directory.Build.targets')
)
foreach ($packInputFile in $packInputFiles) {
    if (-not (Test-Path -LiteralPath $packInputFile -PathType Leaf)) { Fail "pack input '$packInputFile' does not exist." }
    $packInputLines = Get-Content -LiteralPath $packInputFile | Where-Object {
        $_ -match '(?i)(PackageIcon|None Include|_ForbiddenPackageInput Include)'
    }
    foreach ($packInputLine in $packInputLines) {
        if ($packInputLine.IndexOf($windowsPathSeparator) -ge 0) {
            $relativePath = [IO.Path]::GetRelativePath($RepositoryRoot, $packInputFile).Replace($windowsPathSeparator, '/')
            Fail "$relativePath contains a Windows-only pack input path: $($packInputLine.Trim())"
        }
    }
}

$workflowText = Get-Content -Raw -LiteralPath $WorkflowPath
$scriptNames = [System.Collections.Generic.Queue[string]]::new()
$discovered = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

$separatorPattern = '(?:/|' + [regex]::Escape($windowsPathSeparator) + ')'
foreach ($match in [regex]::Matches($workflowText, ('(?i)scripts' + $separatorPattern + '(?<name>[A-Za-z0-9_.-]+[.]ps1)'))) {
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
        if ($defaultText.IndexOf($windowsPathSeparator) -ge 0) {
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
        # Every default must have a repository-derived Join-Path shape. Only a
        # RepositoryRoot container is required to exist; output artifact leaf
        # defaults are intentionally allowed to be absent before the gate packs.
        if ($parameter.Name.VariablePath.UserPath -ceq 'RepositoryRoot' -and
            -not (Test-Path -LiteralPath $resolved -PathType Container)) {
            Fail "$scriptName parameter '$($parameter.Name.VariablePath.UserPath)' does not resolve to a Container on this host: $resolved"
        }
    }

    foreach ($match in [regex]::Matches((Get-Content -Raw -LiteralPath $scriptPath), '(?i)(?<name>[A-Za-z0-9_.-]+[.]ps1)')) {
        $helper = $match.Groups['name'].Value
        if (Test-Path -LiteralPath (Join-Path $RepositoryRoot 'scripts' $helper) -PathType Leaf) {
            $scriptNames.Enqueue($helper)
        }
    }
}

Write-Output "Release PowerShell source-text portability contract: PASS ($($checkedPowerShellFiles.Count) PowerShell source files checked; $($discovered.Count) workflow/helper scripts; pack-sensitive MSBuild inputs checked; defaults use repository-derived path shape, RepositoryRoot containers resolve on this host, and produced artifact leaf defaults need not exist; regex escapes ignored, path-like separators rejected)."
