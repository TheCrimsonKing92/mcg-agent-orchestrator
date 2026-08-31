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

function Is-PrimaryOrchestratorProcess {
    param($Process)

    $command = [string]$Process.CommandLine
    if ([string]::IsNullOrWhiteSpace($command)) {
        return $false
    }

    $name = [string]$Process.Name
    return ($name -eq "dotnet" -or $name -eq "dotnet.exe" -or
            $name -eq "Mcg.AgentOrchestrator.App" -or $name -eq "Mcg.AgentOrchestrator.App.exe") -and
        $command.IndexOf("App.dll", [System.StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Is-DispatchHostProcess {
    param($Process)

    return ([string]$Process.CommandLine).IndexOf(
        "__dispatch-run",
        [System.StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Is-ConductLoopProcess {
    param($Process)

    $command = [string]$Process.CommandLine
    return $command.IndexOf("conduct", [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -and
        $command.IndexOf("--loop", [System.StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Is-OrchestratorLockHolder {
    param($Process)

    $command = [string]$Process.CommandLine
    return $command.IndexOf("App.dll", [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $command.IndexOf("__dispatch-run", [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $command.IndexOf("DispatchProcessHost", [System.StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Is-PotentialOrchestratorProcess {
    param($Process)

    $name = [string]$Process.Name
    return $name -eq "dotnet" -or $name -eq "dotnet.exe" -or
        $name -eq "DispatchProcessHost" -or $name -eq "DispatchProcessHost.exe" -or
        $name.StartsWith("Mcg.AgentOrchestrator", [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-LockKind {
    param($Process)

    $command = [string]$Process.CommandLine
    if ($command.IndexOf("__dispatch-run", [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $command.IndexOf("DispatchProcessHost", [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        return "dispatch-host"
    }

    if ($command.IndexOf("conduct", [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        return "conduct-loop"
    }

    if ($command.IndexOf("serve-dashboard", [System.StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $command.IndexOf("-dashboard", [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
        return "dashboard"
    }

    return "app-host"
}

function Read-OperationProcessInventory {
    $helper = Join-Path $repoRoot "scripts\Get-RepoProcessInfo.ps1"
    # A single-space predicate selects every command line relevant to this snapshot while
    # still making the CLI surface typed failures for records whose command line is unreadable.
    # PowerShell drops an empty-string script argument before it reaches the CLI.
    $lines = @(& $helper -CommandContains " " -Newest ([int]::MaxValue))
    $lastExitCodeVariable = Get-Variable -Name LASTEXITCODE -ErrorAction SilentlyContinue
    $helperExitCode = if ($null -eq $lastExitCodeVariable) { 0 } else { $lastExitCodeVariable.Value }
    $processes = [System.Collections.Generic.List[object]]::new()
    $unavailable = [System.Collections.Generic.List[object]]::new()
    $diagnostics = [System.Collections.Generic.List[string]]::new()
    $complete = $true

    if ($helperExitCode -is [int] -and $helperExitCode -ne 0) {
        $complete = $false
        $diagnostics.Add("PROCESS_QUERY_UNAVAILABLE operation=inventory-acquire exit=$helperExitCode")
    }

    foreach ($entry in $lines) {
        $line = [string]$entry
        if ($line -match '^PROCESS id=(?<id>\d+) parent=(?<parent>\d+) name=(?<name>.*?) created=(?<created>.*?) path=(?<path>.*?) command=(?<command>.*)$') {
            $startedAt = $null
            if (-not [string]::IsNullOrWhiteSpace($Matches.created)) {
                $parsedStartedAt = [System.DateTimeOffset]::MinValue
                if ([System.DateTimeOffset]::TryParse(
                    $Matches.created,
                    [System.Globalization.CultureInfo]::InvariantCulture,
                    [System.Globalization.DateTimeStyles]::RoundtripKind,
                    [ref]$parsedStartedAt)) {
                    $startedAt = $parsedStartedAt
                }
            }

            $processes.Add([pscustomobject]@{
                ProcessId = [int]$Matches.id
                ParentProcessId = [int]$Matches.parent
                Name = $Matches.name
                StartedAt = $startedAt
                ExecutablePath = $Matches.path
                CommandLine = $Matches.command
                RawLine = $line
            })
            continue
        }

        if ($line -match '^PROCESS_QUERY_UNAVAILABLE operation=filter id=(?<id>\d+) name=(?<name>.*?) status=(?<status>\S+)$') {
            $unavailable.Add([pscustomobject]@{
                ProcessId = [int]$Matches.id
                Name = $Matches.name
                Status = $Matches.status
                RawLine = $line
            })
            continue
        }

        if ($line -match '^PROCESS_QUERY_UNAVAILABLE\s') {
            $complete = $false
            $diagnostics.Add($line)
            continue
        }

        if ($line -match '^BACKLOG_CANDIDATE\s') {
            $diagnostics.Add($line)
        }
    }

    return [pscustomobject]@{
        Processes = @($processes)
        Unavailable = @($unavailable)
        Diagnostics = @($diagnostics)
        Complete = $complete
    }
}

function Get-CurrentInvocationProcessIds {
    $excluded = [System.Collections.Generic.HashSet[int]]::new()
    [void]$excluded.Add($PID)
    Write-Output -NoEnumerate $excluded
}

function Select-NewestProcesses {
    param(
        [object[]]$Processes,
        [scriptblock]$Predicate
    )

    return @($Processes |
        Where-Object $Predicate |
        Sort-Object @{ Expression = { if ($null -eq $_.StartedAt) { [System.DateTimeOffset]::MinValue } else { $_.StartedAt } }; Descending = $true },
            @{ Expression = 'ProcessId'; Descending = $true } |
        Select-Object -First $NewestProcesses)
}

function Merge-UniqueProcesses {
    param([object[][]]$Groups)

    $seen = [System.Collections.Generic.HashSet[int]]::new()
    $merged = [System.Collections.Generic.List[object]]::new()
    foreach ($group in $Groups) {
        foreach ($process in $group) {
            if ($seen.Add($process.ProcessId)) {
                $merged.Add($process)
            }
        }
    }

    Write-Output -NoEnumerate ([object[]]$merged.ToArray())
}

function Write-InventoryDiagnostics {
    param(
        $Inventory,
        [object[]]$RelevantUnavailable
    )

    foreach ($diagnostic in $Inventory.Diagnostics) {
        Write-Output $diagnostic
    }

    foreach ($unavailable in $RelevantUnavailable) {
        Write-Output $unavailable.RawLine
    }

    $relevantIds = [System.Collections.Generic.HashSet[int]]::new()
    foreach ($unavailable in $RelevantUnavailable) {
        [void]$relevantIds.Add($unavailable.ProcessId)
    }

    $incidental = @($Inventory.Unavailable | Where-Object { -not $relevantIds.Contains($_.ProcessId) })
    if ($incidental.Count -gt 0) {
        $statusSummary = @($incidental |
            Group-Object Status |
            Sort-Object Name |
            ForEach-Object { "$($_.Name):$($_.Count)" }) -join ','
        Write-Output "PROCESS_QUERY_UNAVAILABLE operation=inventory-summary count=$($incidental.Count) statuses=$statusSummary"
    }
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
$processInventory = $null
$invocationProcessIds = [System.Collections.Generic.HashSet[int]]::new()
$relevantUnavailable = @()
try {
    $processInventory = Read-OperationProcessInventory
    $invocationProcessIds = Get-CurrentInvocationProcessIds
    $relevantUnavailable = @($processInventory.Unavailable | Where-Object { Is-PotentialOrchestratorProcess $_ })
    $eligibleProcesses = @($processInventory.Processes |
        Where-Object { -not $invocationProcessIds.Contains($_.ProcessId) })
    $orchestratorProcesses = Merge-UniqueProcesses -Groups @(
        @(Select-NewestProcesses -Processes $eligibleProcesses -Predicate { Is-PrimaryOrchestratorProcess $_ }),
        @(Select-NewestProcesses -Processes $eligibleProcesses -Predicate { Is-DispatchHostProcess $_ }),
        @(Select-NewestProcesses -Processes $eligibleProcesses -Predicate { Is-ConductLoopProcess $_ }))
    foreach ($process in $orchestratorProcesses) {
        Write-Output $process.RawLine
    }

    Write-InventoryDiagnostics -Inventory $processInventory -RelevantUnavailable $relevantUnavailable
    if ($orchestratorProcesses.Count -eq 0 -and $relevantUnavailable.Count -eq 0 -and $processInventory.Complete) {
        Write-Output "No matching repo processes found."
    }
}
catch {
    Write-Output "process query unavailable: line=$($_.InvocationInfo.ScriptLineNumber) $($_.Exception.Message)"
    Write-Output 'BACKLOG_CANDIDATE title="Snapshot process query degraded" body="Get-OrchestratorSnapshot.ps1 could not run the orchestrator-authored process query; preserve this disposition instead of requesting operator approval."'
}

Write-Section "Build Locks"
try {
    if ($null -eq $processInventory -or -not $processInventory.Complete) {
        Write-Output "lock query unavailable: operation-scoped process inventory was incomplete"
        Write-Output 'BACKLOG_CANDIDATE title="Snapshot lock query degraded" body="Get-OrchestratorSnapshot.ps1 could not acquire a complete process inventory; preserve this disposition instead of requesting operator approval."'
    }
    else {
        $lockHolders = @($processInventory.Processes |
            Where-Object { -not $invocationProcessIds.Contains($_.ProcessId) -and (Is-OrchestratorLockHolder $_) } |
            Sort-Object @{ Expression = { if ($null -eq $_.StartedAt) { [System.DateTimeOffset]::MinValue } else { $_.StartedAt } }; Descending = $true },
                @{ Expression = 'ProcessId'; Descending = $true } |
            Select-Object -First $NewestProcesses)
        foreach ($process in $lockHolders) {
            $kind = Get-LockKind $process
            $created = if ($null -eq $process.StartedAt) { "" } else { $process.StartedAt.ToString('o') }
            Write-Output "LOCK id=$($process.ProcessId) kind=$kind parent=$($process.ParentProcessId) name=$($process.Name) created=$created path=$($process.ExecutablePath) command=$(Short-Command $process.CommandLine)"
        }

        if ($lockHolders.Count -eq 0) {
            if ($relevantUnavailable.Count -gt 0) {
                Write-Output "lock query incomplete: relevant process inspection was unavailable"
            }
            else {
                Write-Output "No orchestrator lock-holders running; in-tree build lock is FREE."
            }
        }
    }
}
catch {
    Write-Output "lock query unavailable: line=$($_.InvocationInfo.ScriptLineNumber) $($_.Exception.Message)"
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
