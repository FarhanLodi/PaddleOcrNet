#requires -Version 7.0
[CmdletBinding()]
param(
    [string] $OutputDirectory = 'artifacts/integration-groups',
    [string] $Filter = '',
    [ValidateRange(1, 20)][int] $ClassesPerGroup = 4,
    [ValidateRange(1, 1024)][int] $ProcessorCount = 4,
    [ValidateRange(10, 14400)][int] $TimeoutSeconds = 1200,
    [switch] $NoBuild
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$project = Join-Path $repoRoot 'test/PaddleOcrNet.Tests/PaddleOcrNet.Tests.csproj'
$output = [IO.Path]::GetFullPath($OutputDirectory, $repoRoot)
$runPath = Join-Path $output ([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
[IO.Directory]::CreateDirectory($runPath) | Out-Null
$env:PADDLEOCRNET_RUN_INTEGRATION = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

function Invoke-DotNet([string[]] $Arguments, [string] $Name) {
    $info = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $info.WorkingDirectory = $repoRoot
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    # Apply only to child dotnet processes (and their testhosts), leaving the caller's environment
    # unchanged. This makes managed CPU-based scheduling reproducible on large shared hosts.
    $info.Environment['DOTNET_PROCESSOR_COUNT'] = $ProcessorCount.ToString([Globalization.CultureInfo]::InvariantCulture)
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $stdoutPath = Join-Path $runPath "$Name.stdout.log"
    $stderrPath = Join-Path $runPath "$Name.stderr.log"
    # Copy both pipes concurrently to disk. FileShare.Read lets humans/CI tail live logs;
    # copying BaseStream avoids PowerShell callbacks on threads without a runspace.
    $stdoutFile = [IO.FileStream]::new($stdoutPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read, 1, [IO.FileOptions]::Asynchronous)
    $stderrFile = [IO.FileStream]::new($stderrPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read, 1, [IO.FileOptions]::Asynchronous)
    $stdoutCopy = $null
    $stderrCopy = $null
    $started = $false
    try {
        if (-not $process.Start()) { throw "Cannot launch dotnet for $Name." }
        $started = $true
        $stdoutCopy = $process.StandardOutput.BaseStream.CopyToAsync($stdoutFile)
        $stderrCopy = $process.StandardError.BaseStream.CopyToAsync($stderrFile)
        $timedOut = $false
        while (-not $process.WaitForExit(1000)) {
            if ($watch.Elapsed.TotalSeconds -ge $TimeoutSeconds) { $timedOut = $true; break }
        }
        if ($timedOut) { $process.Kill($true); $process.WaitForExit() }
        $stdoutCopy.GetAwaiter().GetResult()
        $stderrCopy.GetAwaiter().GetResult()
        $stdoutFile.Flush()
        $stderrFile.Flush()
        $stdoutFile.Dispose()
        $stderrFile.Dispose()
        $text = [IO.File]::ReadAllText($stdoutPath)
        return [pscustomobject]@{ ExitCode = $process.ExitCode; TimedOut = $timedOut; Seconds = $watch.Elapsed.TotalSeconds; Output = $text }
    }
    finally {
        # Also reap children if the script is interrupted or stream copying throws.
        if ($started -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        if ($null -ne $stdoutCopy) { try { $stdoutCopy.GetAwaiter().GetResult() } catch { } }
        if ($null -ne $stderrCopy) { try { $stderrCopy.GetAwaiter().GetResult() } catch { } }
        $stdoutFile.Dispose()
        $stderrFile.Dispose()
        $process.Dispose()
    }
}

$report = [ordered]@{
    StartedUtc = [DateTime]::UtcNow.ToString('o'); Filter = $Filter; ClassesPerGroup = $ClassesPerGroup
    TimeoutSeconds = $TimeoutSeconds; Configuration = 'Release'; IntegrationEnabled = $true
    ProcessorCount = $ProcessorCount; DotNetProcessorCount = $ProcessorCount.ToString([Globalization.CultureInfo]::InvariantCulture)
    Revision = if (Get-Command git -ErrorAction SilentlyContinue) { (& git -C $repoRoot rev-parse HEAD 2>$null | Out-String).Trim() } else { 'unavailable' }
    WorkingTreeDirty = if (Get-Command git -ErrorAction SilentlyContinue) { -not [string]::IsNullOrWhiteSpace((& git -C $repoRoot status --porcelain 2>$null | Out-String)) } else { $null }
    AdditionalRunGates = @(Get-ChildItem Env:PADDLEOCRNET_RUN_* | Sort-Object Name | ForEach-Object { [ordered]@{ Name = $_.Name; Value = $_.Value } })
    Groups = @(); Status = 'running'; Error = $null
}
function Save-Report { $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runPath 'summary.json') -Encoding utf8 }
Save-Report
try {
    $runtime = Invoke-DotNet -Arguments @('--info') -Name 'dotnet-info'
    if ($runtime.ExitCode -ne 0 -or $runtime.TimedOut) { throw 'dotnet --info failed.' }
    if (-not $NoBuild) {
        $build = Invoke-DotNet -Arguments @('build', $project, '-c', 'Release', '-m:1') -Name 'build'
        if ($build.ExitCode -ne 0 -or $build.TimedOut) { throw 'Release build failed or timed out; see build logs.' }
    }
    $discoveryArgs = @('test', $project, '-c', 'Release', '--no-build', '--list-tests')
    if ($Filter) { $discoveryArgs += @('--filter', $Filter) }
    $discovery = Invoke-DotNet -Arguments $discoveryArgs -Name 'discovery'
    if ($discovery.ExitCode -ne 0 -or $discovery.TimedOut) { throw 'Test discovery failed or timed out.' }
    $classes = @($discovery.Output -split '\r?\n' | ForEach-Object {
        if ($_ -match '^\s+(PaddleOcrNet\.Tests\.[A-Za-z0-9_]+)\.') { $Matches[1] }
    } | Sort-Object -Unique)
    if ($classes.Count -eq 0) { throw 'Discovery produced no test classes; refusing to report a passing empty run.' }
    for ($offset = 0; $offset -lt $classes.Count; $offset += $ClassesPerGroup) {
        $selected = @($classes | Select-Object -Skip $offset -First $ClassesPerGroup)
        $name = 'group-{0:D3}' -f ([int]($offset / $ClassesPerGroup) + 1)
        $groupFilter = '(' + (($selected | ForEach-Object { "FullyQualifiedName~$($_)." }) -join '|') + ')'
        if ($Filter) { $groupFilter = "($Filter)&$groupFilter" }
        Write-Host "$name : $($selected -join ', ')"
        $trxPath = Join-Path $runPath "$name.trx"
        $run = Invoke-DotNet -Arguments @('test', $project, '-c', 'Release', '--no-build', '--filter', $groupFilter,
            '--results-directory', $runPath, '--logger', "trx;LogFileName=$name.trx", '--logger', 'console;verbosity=normal', '--', 'RunConfiguration.MaxCpuCount=1', 'xUnit.MaxParallelThreads=1', 'xUnit.ParallelizeTestCollections=false') -Name $name
        $passed = 0; $failed = 0; $skipped = 0; $total = 0; $trxError = $null
        try {
            if (-not (Test-Path -LiteralPath $trxPath)) { throw 'Missing TRX.' }
            [xml] $trx = Get-Content -LiteralPath $trxPath -Raw
            $counters = $trx.TestRun.ResultSummary.Counters
            if ($null -eq $counters) { throw 'Missing TRX counters.' }
            $outcomes = @($trx.TestRun.Results.UnitTestResult | Where-Object { $null -ne $_ })
            $total = $outcomes.Count
            $passed = @($outcomes | Where-Object { $_.outcome -eq 'Passed' }).Count
            $failed = @($outcomes | Where-Object { $_.outcome -eq 'Failed' }).Count
            $skipped = @($outcomes | Where-Object { $_.outcome -eq 'NotExecuted' }).Count
            if ($total -eq 0) { throw 'No test result outcomes recorded.' }
            if ($total -ne [int]$counters.total) { throw "TRX result count $total disagrees with counter total $($counters.total)." }
            if ($passed -ne [int]$counters.passed -or $failed -ne [int]$counters.failed) { throw 'TRX passed/failed outcome counts disagree with summary counters.' }
            if ($passed + $failed + $skipped -ne $total) { throw 'TRX contains unexpected outcomes (for example Aborted, Error or Inconclusive).' }
        } catch { $trxError = $_.Exception.Message }
        $ok = $run.ExitCode -eq 0 -and -not $run.TimedOut -and $failed -eq 0 -and $null -eq $trxError
        $report.Groups += [ordered]@{ Name = $name; Classes = $selected; Filter = $groupFilter; ExitCode = $run.ExitCode
            TimedOut = $run.TimedOut; Seconds = $run.Seconds; Total = $total; Passed = $passed; Failed = $failed
            Skipped = $skipped; TrxError = $trxError; Succeeded = $ok }
        Save-Report
    }
    $report.Status = if (@($report.Groups | Where-Object { -not $_.Succeeded }).Count -eq 0) { 'passed' } else { 'failed' }
    Save-Report
    if ($report.Status -ne 'passed') { throw 'One or more groups failed, timed out or omitted valid TRX output.' }
    Write-Host "Results: $runPath (skips remain explicitly recorded, not passing coverage)."
} catch {
    $report.Status = 'failed'; $report.Error = $_.Exception.Message; Save-Report
    throw
}
