<#
.SYNOPSIS
  Stop exact process ids after optional command-line guard checks.

.DESCRIPTION
  Operator helper for stopping known stale orchestrator processes through the
  repo-approved Invoke-RepoScript.ps1 prefix. This intentionally requires exact
  process ids and can verify that each command line contains one or more expected
  substrings before stopping anything.

.EXAMPLE
  .\scripts\Invoke-RepoScript.ps1 scripts\Stop-RepoProcess.ps1 -Id 1234 -CommandContains acceptance -CommandContains c38f8779 -Force
#>
param(
    [Parameter(Mandatory = $true)]
    [int[]]$Id,

    [string[]]$CommandContains = @(),

    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

foreach ($processId in $Id) {
    $process = Get-CimInstance Win32_Process -Filter "ProcessId=$processId"
    if ($null -eq $process) {
        Write-Output "PROCESS id=$processId status=missing"
        continue
    }

    $command = [string]$process.CommandLine
    foreach ($needle in $CommandContains) {
        if ($command.IndexOf($needle, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
            throw "Refusing to stop process $processId because command line does not contain '$needle'."
        }
    }

    Stop-Process -Id $processId -Force:$Force
    Write-Output "PROCESS id=$processId status=stopped"
}
