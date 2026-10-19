# Runs ON an executor, as mcgrunner. It runs one gate lane for one candidate sha with the repository's
# managed test runner and records status.json, heartbeat.txt, runner.log and the TRX files under the run folder.
# It is safe to start detached: it never depends on the SSH session that launched it.
param(
    [Parameter(Mandatory)][string]$Sha,
    [Parameter(Mandatory)][string]$Project,
    [Parameter(Mandatory)][string]$Filter,
    [Parameter(Mandatory)][string]$Lane,
    [string]$RunRoot = 'C:\mcg-executor',
    # Echo fields from the product job (remote-lane slice 2): the host binds a result only when they match its attempt.
    [string]$AttemptId = '',
    [string]$ExecutorId = '',
    [string]$FilterHash = '',
    [string]$MainSha = '',
    [string]$ManifestIdentity = '',
    # The host's lane budget minus the preamble allowance (job field testHostTimeoutSeconds, goal e4b96116). A job
    # without the field (shadow or focused evidence, or an older host) gets 2100 s, matching the 40 min host lane budget;
    # the test runner's own 780 s default killed 27 laptop lanes with no TRX before 2026-10-17.
    [int]$TestHostTimeoutSeconds = 0
)
$ErrorActionPreference = 'Stop'
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
# tools\bin wins over winget's links: pwsh 7.6 fails to launch the winget-package sqlite3.exe from a
# console-less session ("StandardOutputEncoding is only supported when standard output is redirected"),
# while an unblocked copy here runs (2026-10-07).
$env:Path = (Join-Path $RunRoot 'tools\bin') + ';' + $env:Path
$short = $Sha.Substring(0, 9)
$laneKey = ($Lane -replace '[^A-Za-z0-9]+', '-').Trim('-').ToLowerInvariant()
# One shared worktree per candidate. Its test project is built once, under a per-candidate mutex, by a
# build-only pass; every lane then runs with -NoBuild. Before 2026-10-07 each lane had its own worktree and
# its own cold build, which made remote lanes 1.5-4.4x slower than the same lanes on the host.
$projectId = -join ([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Project.ToLowerInvariant()))[0..3] | ForEach-Object { $_.ToString('x2') })
$work = Join-Path $RunRoot "runs\$short"
$out = Join-Path $RunRoot "results\$short\$laneKey"
New-Item -ItemType Directory -Force -Path $out | Out-Null
$status = Join-Path $out 'status.json'
# The host polls status.json and heartbeat.txt over scp, and sshd holds them open for reading, so a plain write can
# fail with a sharing violation (lease-expired on 2026-10-07 08:07Z). Write a sibling temp file and rename it into
# place, retrying briefly while a poll holds the target.
function Write-FileRetrying([string]$path, [string]$text) {
    $temp = "$path.$PID.tmp"
    for ($attempt = 1; ; $attempt++) {
        try {
            [IO.File]::WriteAllText($temp, $text, [Text.UTF8Encoding]::new($false))
            Move-Item -LiteralPath $temp -Destination $path -Force -ErrorAction Stop
            return
        }
        catch {
            if ($attempt -ge 40) { Remove-Item -LiteralPath $temp -ErrorAction SilentlyContinue; throw }
            Start-Sleep -Milliseconds 250
        }
    }
}
function Write-Status([string]$state, [hashtable]$extra = @{}) {
    $record = [ordered]@{ sha = $Sha; lane = $Lane; project = $Project; filter = $Filter; state = $state; host = $env:COMPUTERNAME; utc = [DateTime]::UtcNow.ToString('o')
        attemptId = $AttemptId; executorId = $ExecutorId; filterHash = $FilterHash; mainSha = $MainSha; manifestIdentity = $ManifestIdentity
        commitSha = $script:observedCommit; treeSha = $script:observedTree }
    foreach ($key in $extra.Keys) { $record[$key] = $extra[$key] }
    Write-FileRetrying $status ($record | ConvertTo-Json -Compress)
}
$heartbeat = Join-Path $out 'heartbeat.txt'
# A separate process writes the heartbeat. Timer events in this script would not fire while it blocks on the test runner.
$beat = "while (Get-Process -Id $PID -ErrorAction SilentlyContinue) { try { [IO.File]::WriteAllText('$heartbeat.beat.tmp', [DateTime]::UtcNow.ToString('o')); Move-Item -LiteralPath '$heartbeat.beat.tmp' -Destination '$heartbeat' -Force -ErrorAction Stop } catch { }; Start-Sleep -Seconds 10 }"
$beater = Start-Process -FilePath (Join-Path $env:ProgramFiles 'PowerShell\7\pwsh.exe') -WindowStyle Hidden -PassThru `
    -ArgumentList '-NoProfile', '-NonInteractive', '-Command', $beat
$started = [DateTime]::UtcNow
# Per-phase seconds reported in status.json so the host can say where laptop lane time went (goal e4b96116).
$phases = [ordered]@{}
function Get-PhaseSeconds([DateTime]$since) { [math]::Round(([DateTime]::UtcNow - $since).TotalSeconds, 1) }
try {
    Write-Status 'preparing'
    # Concurrent lanes share the bare repository; serialize the existence check and worktree add machine-wide.
    $phaseStart = [DateTime]::UtcNow
    $worktreeMutex = [System.Threading.Mutex]::new($false, 'Global\mcg-executor-worktree-add')
    [void]$worktreeMutex.WaitOne()
    try {
        if (-not (Test-Path $work)) { git -C (Join-Path $RunRoot 'repo\repo.git') worktree add -q --detach $work $Sha }
    }
    finally { $worktreeMutex.ReleaseMutex(); $worktreeMutex.Dispose() }
    $script:observedCommit = (git -C $work rev-parse HEAD).Trim()
    $script:observedTree = (git -C $work rev-parse 'HEAD^{tree}').Trim()
    if ($script:observedCommit -ne $Sha) { throw "worktree $work is not at $Sha" }
    $phases.worktreeSeconds = Get-PhaseSeconds $phaseStart
    $results = Join-Path $env:LOCALAPPDATA "Temp\Low\mcg-tests\exec-$short-$laneKey"
    # Build the candidate's test project once per executor with the repository's sanctioned build wrapper and
    # McgIsolatedArtifactsPath set to this worktree. That puts the output and its MSBuild build receipt in
    # <worktree>\bin\<project>\Debug\, the "declared artifact output" MtpTestRunner's -NoBuild path verifies
    # (Select-MtpVerifiedBuildOutput). A build through Invoke-TestSummary itself is not reusable: it builds into a
    # per-invocation run directory. Any nonzero exit fails the lane, and the host then runs it locally.
    $builtMarker = Join-Path $work ".mcg-executor-verified-build-$projectId"
    $buildMutex = [System.Threading.Mutex]::new($false, "Global\mcg-executor-build-$short-$projectId")
    # A lane that died mid-build leaves the mutex abandoned; this lane then owns it and, with no marker, rebuilds.
    $phaseStart = [DateTime]::UtcNow
    try { [void]$buildMutex.WaitOne() } catch [System.Threading.AbandonedMutexException] { }
    $phases.buildWaitSeconds = Get-PhaseSeconds $phaseStart
    try {
        $phases.buildSkipped = [bool](Test-Path -LiteralPath $builtMarker)
        $phases.buildSeconds = 0
        if (-not $phases.buildSkipped) {
            $phaseStart = [DateTime]::UtcNow
            Write-Status 'building'
            Push-Location $work
            try {
                & pwsh -NoProfile -File (Join-Path $work 'scripts\Invoke-WorkerBuildDiagnostic.ps1') -TimeoutSeconds 1800 `
                    build (Join-Path $work $Project) "/p:McgIsolatedArtifactsPath=$work" "/p:Configuration=Debug" *>&1 |
                    Tee-Object -FilePath (Join-Path $out 'prebuild.log') | Out-Null
                $prebuildExit = $LASTEXITCODE
            }
            finally { Pop-Location }
            if ($prebuildExit -ne 0) { throw "prebuild of $Project failed with exit $prebuildExit" }
            Set-Content -LiteralPath $builtMarker -Value ([DateTime]::UtcNow.ToString('o'))
            $phases.buildSeconds = Get-PhaseSeconds $phaseStart
        }
    }
    finally { $buildMutex.ReleaseMutex(); $buildMutex.Dispose() }
    Write-Status 'running' @{ resultsRoot = $results }
    # Test-host CPU, peak working set and laptop available memory for this lane (goal 0fca7ad9).
    $resources = Join-Path $out 'resources.json'
    $sampler = Start-Process -FilePath (Join-Path $env:ProgramFiles 'PowerShell\7\pwsh.exe') -WindowStyle Hidden -PassThru `
        -ArgumentList '-NoProfile', '-NonInteractive', '-File', (Join-Path $RunRoot 'tools\Measure-LaneResources.ps1'), '-Root', $PID, '-Skip', $beater.Id, '-Path', $resources
    Push-Location $work
    try {
        $budget = if ($TestHostTimeoutSeconds -gt 0) { $TestHostTimeoutSeconds } else { 2100 }
        $timeoutArgs = @('-TestHostTimeoutSeconds', $budget)
        $phaseStart = [DateTime]::UtcNow
        & pwsh -NoProfile -File (Join-Path $work 'scripts\Invoke-TestSummary.ps1') -Target $Project -Filter $Filter -NoBuild -ResultsRoot $results @timeoutArgs *>&1 |
            Tee-Object -FilePath (Join-Path $out 'runner.log') | Out-Null
        $exit = $LASTEXITCODE
        $phases.testSeconds = Get-PhaseSeconds $phaseStart
    }
    finally { Pop-Location }
    Get-ChildItem -Path $results -Recurse -Filter *.trx -ErrorAction SilentlyContinue | Copy-Item -Destination $out -Force
    if ($sampler -and -not $sampler.HasExited) { Stop-Process -Id $sampler.Id -Force -ErrorAction SilentlyContinue }
    $completed = @{ exitCode = $exit; seconds = [math]::Round(([DateTime]::UtcNow - $started).TotalSeconds, 1)
        trx = @(Get-ChildItem -Path $out -Filter *.trx | ForEach-Object Name); phases = $phases }
    try {
        $usage = Get-Content -Raw -LiteralPath $resources | ConvertFrom-Json
        foreach ($name in 'testHostCpuSeconds', 'testHostPeakWorkingSetBytes', 'availableMemoryMbMin', 'resourceSamples') {
            if ($null -ne $usage.$name) { $completed[$name] = $usage.$name }
        }
    } catch { }
    Write-Status 'completed' $completed
}
catch {
    Write-Status 'failed' @{ error = $_.Exception.Message; seconds = [math]::Round(([DateTime]::UtcNow - $started).TotalSeconds, 1); phases = $phases }
    throw
}
finally {
    if ($sampler -and -not $sampler.HasExited) { Stop-Process -Id $sampler.Id -Force -ErrorAction SilentlyContinue }
    if ($beater -and -not $beater.HasExited) { Stop-Process -Id $beater.Id -Force -ErrorAction SilentlyContinue }
    Write-FileRetrying $heartbeat ([DateTime]::UtcNow.ToString('o'))
}
