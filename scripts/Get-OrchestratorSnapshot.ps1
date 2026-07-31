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
    [int]$StatusTimeoutSeconds = 20,

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

if ($StatusTimeoutSeconds -lt 1) {
    throw "-StatusTimeoutSeconds must be at least 1."
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

function Quote-ProcessArgument {
    param([string]$Value)

    if ($null -eq $Value) {
        return '""'
    }

    return '"' + $Value.Replace('"', '\"') + '"'
}

function Stop-OwnedProcessTree {
    param([int]$RootProcessId)

    $stopScript = Join-Path $repoRoot "scripts\Stop-RepoProcess.ps1"
    if (Test-Path -LiteralPath $stopScript) {
        try {
            & $stopScript -Id $RootProcessId -Force | Out-Null
            return
        }
        catch {
            Write-Output "process stop helper unavailable for pid=${RootProcessId}: $($_.Exception.Message)"
        }
    }

    if ([System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT) {
        $taskkill = Join-Path $env:SystemRoot "System32\taskkill.exe"
        if (Test-Path -LiteralPath $taskkill) {
            try {
                # Redirect ALL streams, not just stdout: a child that exits between enumeration and kill
                # makes taskkill write "ERROR: The process "N" not found." to stderr, which then surfaces
                # as caller stderr even though the process being gone is the outcome we wanted.
                & $taskkill /PID $RootProcessId /T /F *> $null
                return
            }
            catch {
                Write-Output "process stop taskkill fallback unavailable for pid=${RootProcessId}: $($_.Exception.Message)"
            }
        }
    }

    try {
        $process = [System.Diagnostics.Process]::GetProcessById($RootProcessId)
        $process.Kill($true)
    }
    catch [System.ArgumentException] {
        return
    }
    catch [System.InvalidOperationException] {
        return
    }
    catch {
        Write-Output "process stop fallback unavailable for pid=${RootProcessId}: $($_.Exception.Message)"
    }
}

function Invoke-BoundedGoalStatus {
    param(
        [string]$DotnetPath,
        [string]$AppDllPath,
        [string]$Goal,
        [int]$TimeoutSeconds
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $DotnetPath
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    $startInfo.Arguments = @(
        (Quote-ProcessArgument $AppDllPath),
        "status",
        (Quote-ProcessArgument $Goal)
    ) -join " "

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $killed = $false
    $started = $false
    try {
        [void]$process.Start()
        $started = $true
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()

        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $killed = $true
            $statusProcessId = $process.Id
            try {
                Stop-OwnedProcessTree -RootProcessId $statusProcessId
            }
            catch {
                Write-Output "status timed out after ${TimeoutSeconds}s; failed to kill pid=${statusProcessId}: $($_.Exception.Message)"
            }

            try {
                [void]$process.WaitForExit(5000)
            }
            catch {
                Write-Output "status timed out after ${TimeoutSeconds}s; pid=${statusProcessId} did not confirm exit: $($_.Exception.Message)"
            }

            Write-Output "status timed out after ${TimeoutSeconds}s; killed pid=${statusProcessId}"
        }
        else {
            $process.WaitForExit()
        }

        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if (-not [string]::IsNullOrWhiteSpace($stdout)) {
            Write-Output ($stdout.TrimEnd())
        }

        if (-not [string]::IsNullOrWhiteSpace($stderr)) {
            Write-Output ("status stderr: " + $stderr.TrimEnd())
        }

        if (-not $killed -and $process.ExitCode -ne 0) {
            Write-Output "status exit=$($process.ExitCode)"
        }
    }
    catch {
        Write-Output "status unavailable: $($_.Exception.Message)"
    }
    finally {
        if ($started -and $null -ne $process -and -not $process.HasExited) {
            $statusProcessId = $process.Id
            try {
                Stop-OwnedProcessTree -RootProcessId $statusProcessId
                [void]$process.WaitForExit(5000)
                Write-Output "status cancelled; killed pid=${statusProcessId}"
            }
            catch {
                Write-Output "status cleanup failed for pid=${statusProcessId}: $($_.Exception.Message)"
            }
        }

        if ($null -ne $process) {
            $process.Dispose()
        }
    }
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

Write-Section "Orchestrator Processes"
try {
    & (Join-Path $repoRoot "scripts\Get-RepoProcessInfo.ps1") -Name @("dotnet", "Mcg.AgentOrchestrator.App") -CommandContains App.dll -Newest $NewestProcesses
    & (Join-Path $repoRoot "scripts\Get-RepoProcessInfo.ps1") -DispatchHost -Newest $NewestProcesses
    & (Join-Path $repoRoot "scripts\Get-RepoProcessInfo.ps1") -ConductLoop -Newest $NewestProcesses
}
catch {
    Write-Output "process query unavailable: $($_.Exception.Message)"
    Write-Output 'BACKLOG_CANDIDATE title="Snapshot process query degraded" body="Get-OrchestratorSnapshot.ps1 could not run the orchestrator-authored process query; preserve this disposition instead of requesting operator approval."'
}

Write-Section "Build Locks"
try {
    & (Join-Path $repoRoot "scripts\Get-RepoProcessInfo.ps1") -Locks -Newest $NewestProcesses
}
catch {
    Write-Output "lock query unavailable: $($_.Exception.Message)"
    Write-Output 'BACKLOG_CANDIDATE title="Snapshot lock query degraded" body="Get-OrchestratorSnapshot.ps1 could not run the orchestrator-authored lock query; preserve this disposition instead of requesting operator approval."'
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
            $dotnetPath = if ([string]::IsNullOrWhiteSpace($env:MCG_ORCHESTRATOR_DOTNET_PATH)) { "dotnet" } else { $env:MCG_ORCHESTRATOR_DOTNET_PATH }
            Invoke-BoundedGoalStatus -DotnetPath $dotnetPath -AppDllPath $appDll -Goal $goal -TimeoutSeconds $StatusTimeoutSeconds
        }
    }
}
