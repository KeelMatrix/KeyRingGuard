function Assert-WorkflowSdk([string]$Text, [string]$Version) {
    $jobsIndex = $Text.IndexOf('jobs:', [StringComparison]::Ordinal)
    if ($jobsIndex -lt 0) { throw 'The workflow has no jobs.' }
    $jobs = [regex]::Matches($Text.Substring($jobsIndex), '(?ms)^  (?<name>[a-zA-Z0-9_-]+):[^\S\r\n]*\r?\n(?<body>.*?)(?=^  [a-zA-Z0-9_-]+:|\z)')
    foreach ($job in $jobs) {
        $body = $job.Groups['body'].Value
        if ($body -notmatch 'dotnet|Publish-ReleaseArtifacts|Invoke-LocalGate') { continue }
        $steps = @([regex]::Matches($body, '(?ms)^      - name:.*?(?=^      - name:|\z)') | ForEach-Object Value)
        $setup = @(); $selection = @(); $runs = @()
        for ($index = 0; $index -lt $steps.Count; $index++) {
            if ($steps[$index] -match '(?m)^        uses: actions/setup-dotnet@v4\s*$') { $setup += $index }
            if ($steps[$index] -match '(?m)^        run: pwsh -NoProfile -File scripts/Test-SdkSelection.ps1\s*$') { $selection += $index }
            if ($steps[$index] -match '(?m)^        run:') { $runs += $index }
        }
        if ($setup.Count -ne 1 -or $selection.Count -ne 1 -or $setup[0] -ge $selection[0] -or $selection[0] -ne $runs[0]) {
            throw "Job $($job.Groups['name'].Value) must provision and verify its SDK before CLI execution."
        }
        if ($steps[$setup[0]] -notmatch ('(?m)^          dotnet-version: ' + [regex]::Escape($Version) + '\s*$')) { throw 'Job-local SDK version differs from global.json.' }
        foreach ($step in @($steps[$setup[0]], $steps[$selection[0]])) {
            if ($step -match '(?m)^        (?:if|continue-on-error):') { throw 'SDK prerequisites cannot be conditional or allow failure.' }
        }
    }
}
