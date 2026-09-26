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
# The literal separator allowlist is intentionally empty.
# This contract proves that PowerShell source text and the pack-sensitive MSBuild inputs use
# portable path separators. It does not prove release-job portability for .json, .props,
# NuGet.config, or separators constructed at runtime.
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

foreach ($sourceFile in $checkedPowerShellFiles) {
    $scriptText = Get-Content -Raw -LiteralPath $sourceFile.FullName
    $separatorIndex = $scriptText.IndexOf($windowsPathSeparator)
    if ($separatorIndex -lt 0) { continue }

    $relativePath = [IO.Path]::GetRelativePath($RepositoryRoot, $sourceFile.FullName).Replace($windowsPathSeparator, '/')
    $lineNumber = ($scriptText.Substring(0, $separatorIndex) -split "`n").Count
    Fail "$relativePath contains a literal Windows path separator at line $lineNumber. The portability contract allowlist is empty."
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

Write-Output "Release PowerShell source-text portability contract: PASS ($($checkedPowerShellFiles.Count) PowerShell source files checked; $($discovered.Count) workflow/helper scripts; pack-sensitive MSBuild inputs checked; defaults use repository-derived path shape, RepositoryRoot containers resolve on this host, and produced artifact leaf defaults need not exist; literal separator allowlist: empty)."
