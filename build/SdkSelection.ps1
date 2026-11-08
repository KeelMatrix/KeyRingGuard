function Assert-SdkSelection {
    param($Sdk, [string]$Selected, [switch]$RequireExact)
    if ($Sdk.version -cnotmatch '^[0-9]+[.][0-9]+[.][0-9]+$' -or
        $Sdk.rollForward -cnotin @('disable', 'patch') -or
        $Sdk.allowPrerelease -isnot [bool] -or $Sdk.allowPrerelease) {
        throw 'global.json must select a stable SDK with disable or patch roll-forward and allowPrerelease:false.'
    }
    $required = [version]$Sdk.version
    $valid = $Selected -cmatch '^[0-9]+[.][0-9]+[.][0-9]+$'
    if ($valid) {
        $actual = [version]$Selected
        $valid = $actual -ge $required -and $actual.Major -eq $required.Major -and
            $actual.Minor -eq $required.Minor -and
            [math]::Floor($actual.Build / 100) -eq [math]::Floor($required.Build / 100)
    }
    if ($RequireExact -or $Sdk.rollForward -ceq 'disable') { $valid = $valid -and $Selected -ceq $Sdk.version }
    if (-not $valid) {
        $expectation = if ($RequireExact -or $Sdk.rollForward -ceq 'disable') { 'exactly' } else { 'or a later stable patch in the same feature band as' }
        throw "Required SDK $expectation $($Sdk.version); selected '$Selected'. Install the required SDK and use a compatible Visual Studio/MSBuild version. See docs/releasing.md."
    }
}
