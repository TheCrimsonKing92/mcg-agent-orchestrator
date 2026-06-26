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
    [string[]]$Name = @(),
    [string[]]$CommandContains = @(),
    [int]$Newest = 25,
    [switch]$IncludeChildren
)

$ErrorActionPreference = 'Stop'

if ($Id.Count -eq 0 -and $ParentId.Count -eq 0 -and $Name.Count -eq 0 -and $CommandContains.Count -eq 0) {
    throw 'Specify -Id, -ParentId, -Name, or -CommandContains.'
}

if ($Newest -lt 1) {
    throw '-Newest must be at least 1.'
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

function Add-UniqueProcess {
    param($Process)

    if ($null -eq $Process) {
        return
    }

    if ($querySeen.Add([int]$Process.ProcessId)) {
        $queryResults.Add($Process)
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

function MatchesCommandFilter {
    param($Process)

    if ($CommandContains.Count -eq 0) {
        return $true
    }

    $command = [string]$Process.CommandLine
    foreach ($needle in $CommandContains) {
        if ($command.IndexOf($needle, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
            return $false
        }
    }

    return $true
}

foreach ($processId in $Id) {
    Write-ProcessInfo -ProcessId $processId
}

foreach ($parentProcessId in $ParentId) {
    Get-CimInstance Win32_Process -Filter "ParentProcessId=$parentProcessId" |
        Sort-Object ProcessId |
        ForEach-Object { Write-ProcessInfo -ProcessId ([int]$_.ProcessId) }
}

if ($Name.Count -gt 0 -or $CommandContains.Count -gt 0) {
    $querySeen = [System.Collections.Generic.HashSet[int]]::new()
    $queryResults = [System.Collections.Generic.List[object]]::new()

    if ($Name.Count -gt 0) {
        foreach ($processName in $Name) {
            $escapedName = $processName.Replace("'", "''")
            Get-CimInstance Win32_Process -Filter "Name='$escapedName'" |
                ForEach-Object { Add-UniqueProcess -Process $_ }
        }
    }
    else {
        Get-CimInstance Win32_Process |
            ForEach-Object { Add-UniqueProcess -Process $_ }
    }

    $queryResults |
        Where-Object { MatchesCommandFilter -Process $_ } |
        Sort-Object CreationDate -Descending |
        Select-Object -First $Newest |
        ForEach-Object { Write-ProcessInfo -ProcessId ([int]$_.ProcessId) }
}
