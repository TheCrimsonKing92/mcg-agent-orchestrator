<#
.SYNOPSIS
  List running orchestrator processes that hold the in-tree build lock.

.DESCRIPTION
  Repo-bounded wrapper around the orchestrator CLI process inspector. It avoids
  PowerShell CIM so Codex can run lock checks through Invoke-RepoScript without
  approval prompts or Windows sandbox runner CIM failures.

  Exit code: 0 when the in-tree lock is free, 2 when holders are present.
#>
[CmdletBinding()]
param([switch]$Quiet)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
try {
    $output = @(& (Join-Path $repoRoot 'scripts\Invoke-OrchestratorCommand.ps1') repo-process-info --locks)
    $exitCode = if ($LASTEXITCODE -is [int]) { $LASTEXITCODE } else { 0 }
    if ($exitCode -ne 0) {
        $output
        exit $exitCode
    }

    $locks = @($output | Where-Object { $_ -like 'LOCK id=*' })
    if (-not $Quiet) {
        $output
        if ($locks.Count -gt 0) {
            Write-Output "$($locks.Count) lock-holder(s) running; in-tree build lock is HELD (rebuild will fail until these exit)."
        }
    }

    exit $(if ($locks.Count -gt 0) { 2 } else { 0 })
}
catch {
    Write-Output "PROCESS_QUERY_UNAVAILABLE operation=lock-helper reason=$($_.Exception.GetType().Name): $($_.Exception.Message)"
    Write-Output 'BACKLOG_CANDIDATE title="Orchestrator lock helper degraded" body="Find-OrchestratorLocks.ps1 could not run the orchestrator-authored lock query; preserve this disposition instead of requesting operator approval."'
    exit 1
}
