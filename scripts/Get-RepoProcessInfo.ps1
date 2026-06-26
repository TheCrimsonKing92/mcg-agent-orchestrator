<#
.SYNOPSIS
  Print bounded process details for exact process ids or parent process ids.

.DESCRIPTION
  Operator helper for inspecting orchestrator worker lineage through the
  repo-approved Invoke-RepoScript.ps1 prefix instead of ad-hoc CIM commands.
#>
param(
    [int[]]$Id = @(),
    [int[]]$ParentId = @(),
    [switch]$IncludeChildren
)

$ErrorActionPreference = 'Stop'

if ($Id.Count -eq 0 -and $ParentId.Count -eq 0) {
    throw 'Specify -Id or -ParentId.'
}

$seen = [System.Collections.Generic.HashSet[int]]::new()

function Write-ProcessInfo {
    param([int]$ProcessId)

    if (-not $seen.Add($ProcessId)) {
        return
    }

    $process = Get-CimInstance Win32_Process -Filter "ProcessId=$ProcessId"
    if ($null -eq $process) {
        Write-Output "PROCESS id=$ProcessId status=missing"
        return
    }

    $command = ($process.CommandLine -replace '\s+', ' ').Trim()
    if ($command.Length -gt 420) {
        $command = $command.Substring(0, 420) + '...'
    }

    Write-Output ("PROCESS id={0} parent={1} name={2} created={3} path={4} command={5}" -f `
        $process.ProcessId,
        $process.ParentProcessId,
        $process.Name,
        (Format-CimDate $process.CreationDate),
        $process.ExecutablePath,
        $command)

    if ($IncludeChildren) {
        Get-CimInstance Win32_Process -Filter "ParentProcessId=$ProcessId" |
            Sort-Object ProcessId |
            ForEach-Object { Write-ProcessInfo -ProcessId ([int]$_.ProcessId) }
    }
}

function Format-CimDate {
    param($Value)

    if ($null -eq $Value) {
        return ''
    }

    if ($Value -is [datetime]) {
        return $Value.ToString('o')
    }

    try {
        return ([System.Management.ManagementDateTimeConverter]::ToDateTime([string]$Value)).ToString('o')
    }
    catch {
        return [string]$Value
    }
}

foreach ($processId in $Id) {
    Write-ProcessInfo -ProcessId $processId
}

foreach ($parentProcessId in $ParentId) {
    Get-CimInstance Win32_Process -Filter "ParentProcessId=$parentProcessId" |
        Sort-Object ProcessId |
        ForEach-Object { Write-ProcessInfo -ProcessId ([int]$_.ProcessId) }
}
