function Invoke-BoundedDotnet {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [ValidateRange(1, 120000)][int]$TimeoutMilliseconds = 60000,
        [switch]$IsolatedCredentials
    )

    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    if ($IsolatedCredentials) {
        foreach ($name in @($start.Environment.Keys)) {
            if ($name -match '^(?i:NuGetPackageSourceCredentials_|NUGET_(?:API_KEY|SYMBOL_API_KEY|.*PLUGIN.*|CREDENTIALPROVIDERS_PATH)$|HTTP_PROXY$|HTTPS_PROXY$|ALL_PROXY$)') {
                [void]$start.Environment.Remove($name)
            }
        }
        $start.Environment['NUGET_PLUGIN_PATHS'] = [IO.Path]::Combine([IO.Path]::GetTempPath(), 'no-nuget-plugins-' + [guid]::NewGuid().ToString('N'))
    }
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $started = $false
    try {
        $started = $process.Start()
        if (-not $started) { throw 'Could not start the package probe.' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutMilliseconds)) {
            $process.Kill($true)
            if (-not $process.WaitForExit(5000)) { throw 'Package probe did not terminate after its deadline.' }
            $failure = [TimeoutException]::new('Package probe exceeded its deadline and was terminated.')
            $failure.Data['TimedOut'] = $true
            throw $failure
        }
        $output = $stdout.GetAwaiter().GetResult()
        $errorOutput = $stderr.GetAwaiter().GetResult()
        if ($output) { Write-Output $output.TrimEnd() }
        if ($errorOutput) { Write-Output $errorOutput.TrimEnd() }
        if ($process.ExitCode -ne 0) {
            $failure = [InvalidOperationException]::new("Package probe failed with exit code $($process.ExitCode).")
            $failure.Data['ExitCode'] = $process.ExitCode
            throw $failure
        }
    }
    finally {
        if ($started -and -not $process.HasExited) {
            $process.Kill($true)
            [void]$process.WaitForExit(5000)
        }
        $process.Dispose()
    }
}
