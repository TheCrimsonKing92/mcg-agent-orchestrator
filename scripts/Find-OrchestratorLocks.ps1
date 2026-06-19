<#
.SYNOPSIS
  List running orchestrator processes that hold the in-tree build lock.

.DESCRIPTION
  The launcher rebuilds App.dll on run, so any live `dotnet ... App.dll` process locks
  Core.dll / Infrastructure.dll in App's bin and a rebuild fails (MSB3027) until it exits.
  The usual culprits are detached `__dispatch-run` worker hosts, a `conduct --loop/--watch`
  loop, or a `serve-dashboard`. Run this BEFORE rebuilding/relaunching to see what must
  finish or be killed first.

  Exists to replace ad-hoc `Get-CimInstance Win32_Process | Where-Object { ... }` one-liners,
  which can never match an allowlist prefix and pop a permission prompt every time. This
  script is allowlisted, so the CIM/Where-Object inside it never prompts.

  Exit code: 0 when the in-tree lock is free (no holders), 2 when holders are present, so it
  can gate a rebuild in a conditional.

.EXAMPLE
  .\scripts\Find-OrchestratorLocks.ps1
  .\scripts\Find-OrchestratorLocks.ps1 -Quiet   # no table, just the exit code
#>
[CmdletBinding()]
param([switch]$Quiet)

$procs = Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" -ErrorAction SilentlyContinue |
    Where-Object {
        $_.CommandLine -and (
            $_.CommandLine -like '*App.dll*' -or
            $_.CommandLine -like '*__dispatch-run*' -or
            $_.CommandLine -like '*DispatchProcessHost*')
    }

if (-not $procs) {
    if (-not $Quiet) { Write-Output 'No orchestrator lock-holders running; in-tree build lock is FREE.' }
    exit 0
}

$rows = foreach ($p in $procs) {
    $cl = $p.CommandLine
    $kind =
        if ($cl -like '*__dispatch-run*' -or $cl -like '*DispatchProcessHost*') { 'dispatch-host' }
        elseif ($cl -like '*conduct*--loop*' -or $cl -like '*conduct*--watch*') { 'conduct-loop' }
        elseif ($cl -like '*serve-dashboard*' -or $cl -like '*-dashboard*') { 'dashboard' }
        else { 'app-host' }

    # Surface the goal/task from a dispatch params path in the command line, when present.
    $tag = ''
    if ($cl -match '([0-9a-fA-F]{8})-([0-9a-fA-F]{8})-\d{14}\.dispatch\.json') {
        $tag = "$($Matches[1])/$($Matches[2])"
    }

    [PSCustomObject]@{
        PID     = $p.ProcessId
        Kind    = $kind
        GoalTask = $tag
        Started = $p.CreationDate
    }
}

if (-not $Quiet) {
    $rows | Sort-Object Started | Format-Table -AutoSize
    Write-Output "$(@($rows).Count) lock-holder(s) running; in-tree build lock is HELD (rebuild will fail until these exit)."
}
exit 2
