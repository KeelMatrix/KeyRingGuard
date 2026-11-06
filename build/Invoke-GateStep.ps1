function Invoke-GateStep([string]$Name, [scriptblock]$Command) {
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    Write-Output "== $Name =="
    try {
        $global:LASTEXITCODE = 0
        & $Command
        if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE." }
        Write-Output ("{0}: PASS ({1} ms)" -f $Name, $watch.ElapsedMilliseconds)
    }
    catch {
        Write-Output ("{0}: FAIL ({1} ms)" -f $Name, $watch.ElapsedMilliseconds)
        throw
    }
}
