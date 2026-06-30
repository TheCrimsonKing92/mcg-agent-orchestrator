<#
.SYNOPSIS
  Print a compact operator snapshot without ad-hoc shell pipelines.

.DESCRIPTION
  Combines the common read-only checks used while dogfooding:
  active goals from SQLite, conduct/dispatch process lineage, build-lock holders,
  and optional goal status summaries. This exists so Codex can observe the loop via
  one checked-in script call instead of several permission-prompting snippets.

.EXAMPLE
  .\scripts\Invoke-RepoScript.ps1 scripts\Get-OrchestratorSnapshot.ps1 -GoalPrefix 90445242 95c477e8
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [string[]]$GoalPrefix = @(),
    [int]$ActiveLimit = 8,
    [int]$NewestProcesses = 12,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$AdditionalGoalPrefix = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($ActiveLimit -lt 1) {
    throw "-ActiveLimit must be at least 1."
}

if ($NewestProcesses -lt 1) {
    throw "-NewestProcesses must be at least 1."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$appDll = Join-Path $repoRoot "src\Mcg.AgentOrchestrator.App\bin\Debug\net10.0\Mcg.AgentOrchestrator.App.dll"
$GoalPrefix = @($GoalPrefix) + @($AdditionalGoalPrefix)

function Write-Section {
    param([string]$Name)
    Write-Output ""
    Write-Output "## $Name"
}

function Short-Command {
    param([string]$CommandLine)
    $command = ($CommandLine -replace '\s+', ' ').Trim()
    if ($command.Length -gt 360) {
        return $command.Substring(0, 360) + "..."
    }

    return $command
}

function Format-CimDate {
    param($Value)

    if ($null -eq $Value) {
        return ""
    }

    if ($Value -is [datetime]) {
        return $Value.ToString("s")
    }

    try {
        return ([System.Management.ManagementDateTimeConverter]::ToDateTime([string]$Value)).ToString("s")
    }
    catch {
        return [string]$Value
    }
}

function Is-OrchestratorProcess {
    param($Process)

    $command = [string]$Process.CommandLine
    if ([string]::IsNullOrWhiteSpace($command)) {
        return $false
    }

    return $command.IndexOf("conduct --loop", [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $command.IndexOf("__dispatch-run", [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $command.IndexOf(".dispatch.json", [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $command.IndexOf(".orchestrator\prompts", [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $command.IndexOf(".orchestrator-worktrees", [System.StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Is-OrchestratorLockHolder {
    param($Process)

    $command = [string]$Process.CommandLine
    return $Process.Name -eq "dotnet.exe" -and (
        $command.IndexOf("App.dll", [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $command.IndexOf("__dispatch-run", [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $command.IndexOf("DispatchProcessHost", [System.StringComparison]::OrdinalIgnoreCase) -ge 0)
}

Write-Section "Active Goals"
try {
    & (Join-Path $repoRoot "scripts\Invoke-OrchestratorSqliteTool.ps1") list-goals --status Active --limit $ActiveLimit
    if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) {
        Write-Output "sqlite-tool exit=$LASTEXITCODE"
    }
}
catch {
    Write-Output "active-goals unavailable: $($_.Exception.Message)"
}

$processes = @()
try {
    $processes = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue)
}
catch {
    Write-Section "Processes"
    Write-Output "process query unavailable: $($_.Exception.Message)"
}

if ($processes.Count -gt 0) {
    Write-Section "Orchestrator Processes"
    $interesting = @($processes |
        Where-Object { Is-OrchestratorProcess -Process $_ } |
        Sort-Object CreationDate -Descending |
        Select-Object -First $NewestProcesses)

    if ($interesting.Count -eq 0) {
        Write-Output "No conduct/dispatch worker processes found."
    }
    else {
        foreach ($process in $interesting) {
            Write-Output ("PROCESS id={0} parent={1} name={2} created={3} command={4}" -f `
                $process.ProcessId,
                $process.ParentProcessId,
                $process.Name,
                (Format-CimDate $process.CreationDate),
                (Short-Command ([string]$process.CommandLine)))
        }
    }

    Write-Section "Build Locks"
    $locks = @($processes |
        Where-Object { Is-OrchestratorLockHolder -Process $_ } |
        Sort-Object CreationDate)

    if ($locks.Count -eq 0) {
        Write-Output "No orchestrator lock-holders running; in-tree build lock is FREE."
    }
    else {
        foreach ($process in $locks) {
            $command = [string]$process.CommandLine
            $kind =
                if ($command.IndexOf("__dispatch-run", [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
                    $command.IndexOf("DispatchProcessHost", [System.StringComparison]::OrdinalIgnoreCase) -ge 0) { "dispatch-host" }
                elseif ($command.IndexOf("conduct", [System.StringComparison]::OrdinalIgnoreCase) -ge 0) { "conduct-loop" }
                elseif ($command.IndexOf("serve-dashboard", [System.StringComparison]::OrdinalIgnoreCase) -ge 0) { "dashboard" }
                else { "app-host" }

            Write-Output ("LOCK id={0} kind={1} created={2}" -f `
                $process.ProcessId,
                $kind,
                (Format-CimDate $process.CreationDate))
        }
    }
}

if ($GoalPrefix.Count -gt 0) {
    Write-Section "Goal Status"
    if (-not (Test-Path -LiteralPath $appDll -PathType Leaf)) {
        Write-Output "App DLL not found: $appDll"
    }
    else {
        foreach ($goal in $GoalPrefix) {
            if ([string]::IsNullOrWhiteSpace($goal)) {
                continue
            }

            Write-Output ""
            Write-Output "### $goal"
            try {
                & dotnet $appDll status $goal
                if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) {
                    Write-Output "status exit=$LASTEXITCODE"
                }
            }
            catch {
                Write-Output "status unavailable: $($_.Exception.Message)"
            }
        }
    }
}
