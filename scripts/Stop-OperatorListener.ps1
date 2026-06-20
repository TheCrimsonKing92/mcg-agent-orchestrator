<#
.SYNOPSIS
  Stop the orchestrator's `operator-listen` Discord listener cleanly and idempotently.

.DESCRIPTION
  The operator-listen listener is a long-lived `dotnet.exe` process. It may run from the
  in-tree bin/Debug build or from an isolated copy under %TEMP%\mcg-run\<hash>\. Stopping it
  used to require an ad-hoc `Get-CimInstance Win32_Process | Where-Object { ... } | Stop-Process`
  one-liner, which can never match an allowlist prefix and pops a permission prompt every time.

  This script is allowlisted, so the CIM query + Stop-Process inside it never prompt. It is
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

# Precise, conjunctive match: dotnet.exe host carrying BOTH orchestrator markers.
# Anything that is not a dotnet.exe process (e.g. codex/codex.exe) can never be returned here.
$procs = Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" -ErrorAction SilentlyContinue |
    Where-Object {
        $_.CommandLine -and
        ($_.CommandLine -like '*operator-listen*') -and
        ($_.CommandLine -like '*Mcg.AgentOrchestrator.App*')
    }

if (-not $procs) {
    Write-Output 'No operator-listen process running.'
    exit 0
}

if ($DryRun) {
    foreach ($p in $procs) {
        Write-Output "Would stop operator-listen (pid $($p.ProcessId))"
    }
    exit 0
}

foreach ($p in $procs) {
    Stop-Process -Id $p.ProcessId -Force -ErrorAction SilentlyContinue
    Write-Output "Stopped operator-listen (pid $($p.ProcessId))"
}
exit 0
