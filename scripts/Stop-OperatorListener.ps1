<#
.SYNOPSIS
  Stop the orchestrator's `operator-listen` Discord listener cleanly and idempotently.

.DESCRIPTION
  The operator-listen listener is a long-lived `dotnet.exe` process. It may run from the
  in-tree bin/Debug build or from an isolated copy under %TEMP%\mcg-run\<hash>\. Stopping it
  uses the repository process-inspection command so lookup and stop both use the shared native seam.

  This script is allowlisted, so the bounded query and guarded stop do not prompt. It is
  IDEMPOTENT: it exits 0 whether or not a listener was running.

  SAFETY: it matches ONLY processes named `dotnet.exe` whose CommandLine contains BOTH
  'operator-listen' AND 'Mcg.AgentOrchestrator.App'. It will never stop a process that is not
  dotnet.exe (so codex / codex.exe is never touched) and it ignores any process missing either
  marker. PID 10484 (the operator's personal codex) cannot match because it is not a dotnet host
  carrying both orchestrator markers.

  Exit code: always 0 (nothing running, stopped successfully, or -DryRun listing).

.PARAMETER DryRun
  List the matching pid(s) without stopping anything. Alias: -WhatIf.

.EXAMPLE
  pwsh -NoProfile -File scripts/Stop-OperatorListener.ps1

.EXAMPLE
  pwsh -NoProfile -File scripts/Stop-OperatorListener.ps1 -DryRun
#>
[CmdletBinding()]
param(
    [Alias('WhatIf')]
    [switch]$DryRun
)

$repoRoot = Split-Path -Parent $PSScriptRoot
$queryOutput = @(& (Join-Path $repoRoot 'scripts\Get-RepoProcessInfo.ps1') `
    -Name dotnet `
    -CommandContains @('operator-listen', 'Mcg.AgentOrchestrator.App') `
    -Newest 25)
if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) {
    Write-Output 'Operator-listen query unavailable; stopped nothing.'
    exit 0
}

$processIds = @($queryOutput | ForEach-Object {
    if ($_ -match '^PROCESS id=(\d+)\s') { [int]$Matches[1] }
})

if ($processIds.Count -eq 0) {
    Write-Output 'No operator-listen process running.'
    exit 0
}

if ($DryRun) {
    foreach ($processId in $processIds) {
        Write-Output "Would stop operator-listen (pid $processId)"
    }
    exit 0
}

foreach ($processId in $processIds) {
    & (Join-Path $repoRoot 'scripts\Stop-RepoProcess.ps1') `
        -Id $processId `
        -CommandContains @('operator-listen', 'Mcg.AgentOrchestrator.App') `
        -Force
}
exit 0
