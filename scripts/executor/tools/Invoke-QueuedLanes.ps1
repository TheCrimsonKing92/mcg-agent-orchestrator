# Runs ON an executor as the action of the scheduled task "mcg-executor-lane" (principal mcgrunner, S4U).
# It drains C:\mcg-executor\queue\*.json oldest first, running up to $Slots lanes at once. Each job is
# {"sha","project","filter","lane",...}. A taken job moves to queue\taken\ before it runs, so a retrigger never
# runs it twice. Task Scheduler ignores a retrigger while this instance runs, so the loop keeps polling and picks up
# jobs queued after it started; it exits only when the queue is empty and no child runs.
# Task Scheduler owns these processes, so a dropped SSH connection cannot stop a lane.
param(
    [string]$RunRoot = 'C:\mcg-executor',
    # Defaults to the number in $RunRoot\slots.txt (written by the operator to match the host's "slots"), else 1.
    [int]$Slots = 0
)
$ErrorActionPreference = 'Stop'
$queue = Join-Path $RunRoot 'queue'
$taken = Join-Path $queue 'taken'
New-Item -ItemType Directory -Force -Path $taken | Out-Null
if ($Slots -lt 1) {
    $slotsFile = Join-Path $RunRoot 'slots.txt'
    $Slots = 1
    if (Test-Path -LiteralPath $slotsFile) {
        $parsed = 0
        if ([int]::TryParse((Get-Content -Raw -LiteralPath $slotsFile).Trim(), [ref]$parsed) -and $parsed -ge 1 -and $parsed -le 16) { $Slots = $parsed }
    }
}
$pwsh = Join-Path $env:ProgramFiles 'PowerShell\7\pwsh.exe'
if (-not (Test-Path -LiteralPath $pwsh)) { $pwsh = (Get-Command pwsh -CommandType Application | Select-Object -First 1).Source }
$jobScript = Join-Path $RunRoot 'tools\Invoke-ExecutorJob.ps1'
$running = [System.Collections.Generic.List[object]]::new()
# The host reads $RunRoot\runner-state.json before offering a lane (occupancyProbe) and treats a state older than
# 120 s as not busy, so it is rewritten every loop pass and emptied on exit. Written atomically: temp then rename.
$statePath = Join-Path $RunRoot 'runner-state.json'
function Write-RunnerState([string[]]$RunningNames, [string[]]$QueuedNames) {
    $state = [ordered]@{
        utc = [DateTime]::UtcNow.ToString('o'); slots = $Slots
        running = @($RunningNames | Where-Object { $_ }); queued = @($QueuedNames | Where-Object { $_ })
    }
    $temp = "$statePath.tmp"
    try {
        Set-Content -LiteralPath $temp -Value ($state | ConvertTo-Json -Compress) -Encoding utf8
        Move-Item -LiteralPath $temp -Destination $statePath -Force
    } catch { }
}
while ($true) {
    # The host also writes <job base name>.cancel for a job that already started (it abandoned the lane).
    # Stop that job's whole process tree so it frees its slot instead of running to completion.
    for ($i = $running.Count - 1; $i -ge 0; $i--) {
        $child = $running[$i]
        $cancel = Join-Path $queue ($child.BaseName + '.cancel')
        if (-not $child.Process.HasExited -and (Test-Path -LiteralPath $cancel)) {
            & taskkill.exe /PID $child.Process.Id /T /F 2>&1 | Out-Null
            Move-Item -LiteralPath $cancel -Destination (Join-Path $taken ($child.BaseName + '.cancel')) -Force -ErrorAction SilentlyContinue
            Add-Content -Path (Join-Path $taken 'cancelled.log') -Value "$([DateTime]::UtcNow.ToString('o')) $($child.Name) started"
            if (-not (Test-Path -LiteralPath $child.ErrorFile)) {
                Set-Content -Path $child.ErrorFile -Value "$([DateTime]::UtcNow.ToString('o')) $($child.Name): cancelled by host after start"
            }
        }
    }
    # Reap exited children; a nonzero exit without its own error file gets one.
    for ($i = $running.Count - 1; $i -ge 0; $i--) {
        $child = $running[$i]
        if ($child.Process.HasExited) {
            if ($child.Process.ExitCode -ne 0 -and -not (Test-Path -LiteralPath $child.ErrorFile)) {
                Set-Content -Path $child.ErrorFile -Value "$([DateTime]::UtcNow.ToString('o')) $($child.Name): exit $($child.Process.ExitCode)"
            }
            $running.RemoveAt($i)
        }
    }
    $queued = @(Get-ChildItem -Path $queue -Filter *.json -File | Sort-Object LastWriteTimeUtc)
    Write-RunnerState ($running | ForEach-Object { $_.BaseName }) ($queued | ForEach-Object { $_.BaseName })
    if ($queued.Count -eq 0 -and $running.Count -eq 0) { break }
    foreach ($next in $queued) {
        if ($running.Count -ge $Slots) { break }
        $claimed = Join-Path $taken $next.Name
        try { Move-Item -Path $next.FullName -Destination $claimed -ErrorAction Stop } catch { continue }
        # The host writes <job base name>.cancel when it abandons a lane before the job starts.
        $cancel = Join-Path $queue ($next.BaseName + '.cancel')
        if (Test-Path -LiteralPath $cancel) {
            Move-Item -LiteralPath $cancel -Destination (Join-Path $taken ($next.BaseName + '.cancel')) -ErrorAction SilentlyContinue
            Add-Content -Path (Join-Path $taken 'cancelled.log') -Value "$([DateTime]::UtcNow.ToString('o')) $($next.Name)"
            continue
        }
        $process = Start-Process -FilePath $pwsh -WindowStyle Hidden -PassThru `
            -ArgumentList '-NoProfile', '-NonInteractive', '-File', $jobScript, '-JobPath', $claimed, '-RunRoot', $RunRoot
        $running.Add([pscustomobject]@{ Process = $process; Name = $next.Name; BaseName = $next.BaseName
                ErrorFile = [IO.Path]::ChangeExtension($claimed, '.error.txt') })
    }
    if ($running.Count -gt 0) {
        try { Wait-Process -Id ($running | ForEach-Object { $_.Process.Id }) -Timeout 2 -ErrorAction SilentlyContinue } catch { }
    }
    else { Start-Sleep -Seconds 2 }
}
Write-RunnerState @() @()
# Markers whose job had already finished are moved aside so they do not accumulate.
Get-ChildItem -Path $queue -File | Where-Object Extension -eq '.cancel' |
    Move-Item -Destination $taken -ErrorAction SilentlyContinue
# Per-lane worktrees accumulate; prune ones untouched for three days through git so its metadata stays consistent.
$repo = Join-Path $RunRoot 'repo\repo.git'
Get-ChildItem -Path (Join-Path $RunRoot 'runs') -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.LastWriteTimeUtc -lt [DateTime]::UtcNow.AddDays(-3) } |
    ForEach-Object { git -C $repo worktree remove --force $_.FullName 2>$null | Out-Null }
git -C $repo worktree prune 2>$null | Out-Null
