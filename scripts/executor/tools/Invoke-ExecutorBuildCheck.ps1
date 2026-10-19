# Runs a build-only completion-evidence job on an executor. Results are retained per candidate.
param(
    [Parameter(Mandatory)][string]$JobPath,
    [string]$RunRoot = 'C:\mcg-executor'
)
$ErrorActionPreference = 'Stop'
$job = Get-Content -Raw -LiteralPath $JobPath | ConvertFrom-Json
$sha = [string]$job.sha
if ($sha -notmatch '^[A-Fa-f0-9]{40,64}$') { throw 'Invalid build candidate sha.' }
$short = $sha.Substring(0, 9)
$work = Join-Path $RunRoot "build\.orchestrator-worktrees\b-$short"
$out = Join-Path $RunRoot "results\$short\worker-build-check"
New-Item -ItemType Directory -Force -Path $out | Out-Null
$status = Join-Path $out 'status.json'
$heartbeat = Join-Path $out 'heartbeat.txt'
$phases = [ordered]@{}
$started = [DateTime]::UtcNow
$observedCommit = ''
$observedTree = ''
$beater = $null
$candidateHeld = $false
$candidateMutex = [Threading.Mutex]::new($false, "Global\mcg-executor-build-check-$short")
$pwsh = (Get-Process -Id $PID).Path
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
    $record = [ordered]@{ sha = $sha; lane = $job.lane; project = $job.project; filter = $job.filter
        state = $state; host = $env:COMPUTERNAME; utc = [DateTime]::UtcNow.ToString('o')
        kind = 'build-check'; commitSha = $script:observedCommit; treeSha = $script:observedTree }
    foreach ($name in 'attemptId', 'executorId', 'filterHash', 'mainSha', 'manifestIdentity') {
        $record[$name] = if ($job.PSObject.Properties[$name]) { [string]$job.$name } else { '' }
    }
    foreach ($key in $extra.Keys) { $record[$key] = $extra[$key] }
    Write-FileRetrying $status ($record | ConvertTo-Json -Depth 6 -Compress)
}
function Get-PhaseSeconds([DateTime]$since) { [math]::Round(([DateTime]::UtcNow - $since).TotalSeconds, 1) }
try {
    $phaseStart = [DateTime]::UtcNow
    try { [void]$candidateMutex.WaitOne() } catch [Threading.AbandonedMutexException] { }
    $candidateHeld = $true
    $phases.buildWaitSeconds = Get-PhaseSeconds $phaseStart
    Write-FileRetrying $heartbeat ([DateTime]::UtcNow.ToString('o'))
    # Separate process: the heartbeat must continue while the helper blocks this script.
    $escapedHeartbeat = $heartbeat.Replace("'", "''")
    $beat = "while (Get-Process -Id $PID -ErrorAction SilentlyContinue) { try { [IO.File]::WriteAllText('$escapedHeartbeat.beat.tmp', [DateTime]::UtcNow.ToString('o')); Move-Item -LiteralPath '$escapedHeartbeat.beat.tmp' -Destination '$escapedHeartbeat' -Force -ErrorAction Stop } catch { }; Start-Sleep -Seconds 10 }"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($beat))
    $beater = Start-Process -FilePath $pwsh -WindowStyle Hidden -PassThru -ArgumentList '-NoProfile', '-NonInteractive', '-EncodedCommand', $encoded
    Write-Status 'preparing'
    $phaseStart = [DateTime]::UtcNow
    $worktreeMutex = [Threading.Mutex]::new($false, 'Global\mcg-executor-worktree-add')
    try {
        try { [void]$worktreeMutex.WaitOne() } catch [Threading.AbandonedMutexException] { }
        try {
            if (-not (Test-Path -LiteralPath $work)) {
                git -C (Join-Path $RunRoot 'repo\repo.git') worktree add -q --detach $work $sha
                if ($LASTEXITCODE -ne 0) { throw "Cannot create build worktree for $sha" }
            }
        }
        finally { $worktreeMutex.ReleaseMutex() }
    }
    finally { $worktreeMutex.Dispose() }
    $script:observedCommit = (git -C $work rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $script:observedCommit -ne $sha) { throw "worktree $work is not at $sha" }
    $script:observedTree = (git -C $work rev-parse 'HEAD^{tree}').Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve build candidate tree.' }
    $phases.worktreeSeconds = Get-PhaseSeconds $phaseStart
    $projects = @($job.projects)
    if ($projects.Count -eq 0 -or @($projects | Where-Object { $_ -isnot [string] -or [string]::IsNullOrWhiteSpace($_) }).Count -ne 0) {
        throw 'Build projects must be a non-empty string array.'
    }
    Import-Module (Join-Path $work 'scripts\WorkerBuildReceipt.psm1') -Force
    $receipt = Join-Path (Get-WorkerBuildRunRoot -Root $work) 'artifacts\worker-build-receipt.json'
    Remove-Item -LiteralPath $receipt -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path $out 'worker-build-receipt.json') -ErrorAction SilentlyContinue
    Write-Status 'building'
    $phaseStart = [DateTime]::UtcNow
    Push-Location $work
    try {
        & $pwsh -NoProfile -File (Join-Path $work 'scripts\Invoke-WorkerBuildCheck.ps1') @projects *>&1 |
            Tee-Object -FilePath (Join-Path $out 'build-output.txt') | Out-Null
        $buildExit = $LASTEXITCODE
    }
    finally { Pop-Location }
    $phases.buildSeconds = Get-PhaseSeconds $phaseStart
    $files = @('build-output.txt')
    if (Test-Path -LiteralPath $receipt) {
        Copy-Item -LiteralPath $receipt -Destination (Join-Path $out 'worker-build-receipt.json') -Force
        $files += 'worker-build-receipt.json'
    }
    Write-Status 'completed' @{ exitCode = $buildExit; files = $files; trx = @(); phases = $phases; seconds = (Get-PhaseSeconds $started) }
}
catch {
    Write-Status 'failed' @{ error = $_.Exception.Message; phases = $phases; seconds = (Get-PhaseSeconds $started) }
    throw
}
finally {
    if ($beater) {
        try { if (-not $beater.HasExited) { $beater.Kill(); $beater.WaitForExit() } } finally { $beater.Dispose() }
    }
    if ($candidateHeld) {
        Write-FileRetrying $heartbeat ([DateTime]::UtcNow.ToString('o'))
        $candidateMutex.ReleaseMutex()
    }
    $candidateMutex.Dispose()
}
