<#
.SYNOPSIS
  Resume the last journaled unattended conduct loop when no healthy loop is running.

.DESCRIPTION
  Idempotent reboot/crash/wedge recovery for operator drives. A live conduct process
  is healthy only while its event stream is recent. A stale process is terminated
  only after its PID and start time match the conduct-loop lock, then the journaled
  drive is relaunched. Recovery receipts are appended to auto-resume.log.

.EXAMPLE
  pwsh -NoProfile -File scripts/Resume-OrchestratorLoop.ps1
#>
[CmdletBinding()]
param(
    [string]$JournalPath,
    [string]$TaskName = "McgOrchestratorAutoResume",

    # Forty default 15-second polls gives a 10-minute window, twice the conductor's
    # five-minute long-gate heartbeat interval.
    [ValidateRange(1, 2147483647)]
    [int]$StaleAfterPollIntervals = 40
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-JournalPath {
    param([string]$RepositoryRoot, [string]$ConfiguredPath)

    if ([string]::IsNullOrWhiteSpace($ConfiguredPath)) {
        return Join-Path $RepositoryRoot ".orchestrator\last-drive.json"
    }

    return [System.IO.Path]::GetFullPath($ConfiguredPath)
}

function Resolve-NextBatchName {
    param([string]$CurrentName)

    if ([string]::IsNullOrWhiteSpace($CurrentName)) {
        $CurrentName = "conduct-loop"
    }

    $match = [System.Text.RegularExpressions.Regex]::Match(
        $CurrentName,
        "^(?<prefix>.*?)(?<number>\d+)$",
        [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $match.Success) {
        return "$CurrentName-handoff-1"
    }

    $digits = $match.Groups["number"].Value
    $next = [long]::Parse($digits, [System.Globalization.CultureInfo]::InvariantCulture) + 1
    return $match.Groups["prefix"].Value + $next.ToString(("0" * $digits.Length), [System.Globalization.CultureInfo]::InvariantCulture)
}

function Get-ConductLoopProcesses {
    param([string]$RepositoryRoot)

    $helperPath = Join-Path $RepositoryRoot "scripts\Get-RepoProcessInfo.ps1"
    if (-not (Test-Path -LiteralPath $helperPath -PathType Leaf)) {
        throw "Repo process helper not found: $helperPath"
    }

    $powerShellPath = (Get-Process -Id $PID).Path
    if ([string]::IsNullOrWhiteSpace($powerShellPath)) {
        $powerShellPath = "powershell.exe"
    }

    $output = @(& $powerShellPath -NoProfile -ExecutionPolicy Bypass -File $helperPath -ConductLoop -Newest 10 2>&1)
    $helperExitCode = $LASTEXITCODE
    if ($helperExitCode -is [int] -and $helperExitCode -ne 0) {
        throw "Repo process helper failed with exit code $helperExitCode`: $($output -join ' ')"
    }

    $processes = @()
    foreach ($line in $output) {
        $text = [string]$line
        $processMatch = [regex]::Match($text, "^PROCESS\s+id=(?<id>\d+)\s+(?<detail>.*)$")
        if (-not $processMatch.Success -or $text -match "status=missing") {
            continue
        }

        $createdAt = $null
        $createdMatch = [regex]::Match($processMatch.Groups["detail"].Value, "(?:^|\s)created=(?<created>\S+)")
        if ($createdMatch.Success) {
            $parsedCreatedAt = [DateTimeOffset]::MinValue
            if ([DateTimeOffset]::TryParse(
                    $createdMatch.Groups["created"].Value,
                    [System.Globalization.CultureInfo]::InvariantCulture,
                    [System.Globalization.DateTimeStyles]::RoundtripKind,
                    [ref]$parsedCreatedAt)) {
                $createdAt = $parsedCreatedAt.ToUniversalTime()
            }
        }

        $processes += [pscustomobject]@{
            Id = [int]$processMatch.Groups["id"].Value
            CreatedAt = $createdAt
            Detail = $text
        }
    }

    return @($processes)
}

function Get-ConductLockOwner {
    param([string]$RepositoryRoot)

    $lockPath = Join-Path $RepositoryRoot ".orchestrator\conduct-loop.lock"
    if (-not (Test-Path -LiteralPath $lockPath -PathType Leaf)) {
        return $null
    }

    $lines = @(Get-Content -LiteralPath $lockPath)
    $ownerId = 0
    $ownerStartedAt = [DateTimeOffset]::MinValue
    if ($lines.Count -lt 3 -or
        -not [int]::TryParse($lines[0], [ref]$ownerId) -or
        -not [DateTimeOffset]::TryParseExact(
            $lines[2],
            "O",
            [System.Globalization.CultureInfo]::InvariantCulture,
            [System.Globalization.DateTimeStyles]::RoundtripKind,
            [ref]$ownerStartedAt)) {
        throw "Conduct-loop lock identity is invalid: $lockPath"
    }

    return [pscustomobject]@{
        Id = $ownerId
        StartedAt = $ownerStartedAt.ToUniversalTime()
        Path = $lockPath
    }
}

function Get-PollSeconds {
    param($Journal)

    $pollSeconds = 0
    $property = $Journal.PSObject.Properties["pollSeconds"]
    if ($null -ne $property -and [int]::TryParse([string]$property.Value, [ref]$pollSeconds) -and $pollSeconds -gt 0) {
        return $pollSeconds
    }

    $journalArguments = @($Journal.arguments | ForEach-Object { [string]$_ })
    for ($index = 0; $index -lt ($journalArguments.Count - 1); $index++) {
        if ($journalArguments[$index].Equals("--poll-seconds", [System.StringComparison]::OrdinalIgnoreCase) -and
            [int]::TryParse($journalArguments[$index + 1], [ref]$pollSeconds) -and
            $pollSeconds -gt 0) {
            return $pollSeconds
        }
    }

    return 15
}

function Get-ConductLiveness {
    param(
        [string]$RepositoryRoot,
        $Process,
        [int]$PollSeconds,
        [int]$PollIntervals
    )

    $eventLogPath = Join-Path $RepositoryRoot ".orchestrator\logs\conduct-events.log"
    $lastProgressAt = $Process.CreatedAt
    if (Test-Path -LiteralPath $eventLogPath -PathType Leaf) {
        $eventWrittenAt = [DateTimeOffset](Get-Item -LiteralPath $eventLogPath).LastWriteTimeUtc
        if ($null -eq $lastProgressAt -or $eventWrittenAt -gt $lastProgressAt) {
            $lastProgressAt = $eventWrittenAt
        }
    }

    if ($null -eq $lastProgressAt) {
        return [pscustomobject]@{
            EvidenceAvailable = $false
            IsHealthy = $false
            Age = $null
            Threshold = [TimeSpan]::FromSeconds([double]$PollSeconds * $PollIntervals)
            EventLogPath = $eventLogPath
        }
    }

    $age = [DateTimeOffset]::UtcNow - $lastProgressAt
    if ($age -lt [TimeSpan]::Zero) {
        $age = [TimeSpan]::Zero
    }
    $threshold = [TimeSpan]::FromSeconds([double]$PollSeconds * $PollIntervals)
    return [pscustomobject]@{
        EvidenceAvailable = $true
        IsHealthy = $age -le $threshold
        Age = $age
        Threshold = $threshold
        EventLogPath = $eventLogPath
    }
}

function Write-ResumeReceipt {
    param(
        [string]$RepositoryRoot,
        [string]$Message,
        [switch]$Required
    )

    [Console]::Out.WriteLine($Message)
    [Console]::Out.Flush()
    try {
        $logDirectory = Join-Path $RepositoryRoot ".orchestrator\logs"
        New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
        $timestamp = [DateTimeOffset]::UtcNow.ToString("O", [System.Globalization.CultureInfo]::InvariantCulture)
        Add-Content -LiteralPath (Join-Path $logDirectory "auto-resume.log") -Value "$timestamp $Message"
    }
    catch {
        if ($Required) {
            throw "Auto-resume recovery receipt could not be persisted: $($_.Exception.Message)"
        }
    }
}

function Stop-ConfirmedConductProcess {
    param(
        [string]$RepositoryRoot,
        $Process,
        $LockOwner,
        $Liveness
    )

    if ($null -eq $LockOwner -or $Process.Id -ne $LockOwner.Id) {
        throw "Stale conduct process cannot be terminated because it is not the confirmed conduct-loop lock owner."
    }
    if ($null -eq $Process.CreatedAt -or $Process.CreatedAt -ne $LockOwner.StartedAt) {
        throw "Stale conduct process cannot be terminated because its start time does not match the conduct-loop lock."
    }

    $target = Get-Process -Id $LockOwner.Id -ErrorAction Stop
    $actualStartedAt = [DateTimeOffset]$target.StartTime.ToUniversalTime()
    if ($actualStartedAt -ne $LockOwner.StartedAt) {
        $target.Dispose()
        throw "Stale conduct process cannot be terminated because PID $($LockOwner.Id) was recycled."
    }

    $ageSeconds = [Math]::Floor($Liveness.Age.TotalSeconds)
    $thresholdSeconds = [Math]::Floor($Liveness.Threshold.TotalSeconds)
    Write-ResumeReceipt -RepositoryRoot $RepositoryRoot -Required -Message (
        "RESUME_RECOVERY incumbentPid=$($LockOwner.Id) action=terminate eventAgeSeconds=$ageSeconds thresholdSeconds=$thresholdSeconds")
    try {
        Stop-Process -Id $LockOwner.Id -Force -ErrorAction Stop
        if (-not $target.WaitForExit(10000)) {
            throw "process did not exit within 10 seconds"
        }
    }
    catch {
        Write-ResumeReceipt -RepositoryRoot $RepositoryRoot -Required -Message (
            "RESUME_RECOVERY_FAILED incumbentPid=$($LockOwner.Id) reason=termination-failed detail=$($_.Exception.Message)")
        throw "Failed to terminate stale conduct lock owner PID $($LockOwner.Id): $($_.Exception.Message)"
    }
    finally {
        $target.Dispose()
    }

    Write-ResumeReceipt -RepositoryRoot $RepositoryRoot -Required -Message (
        "RESUME_RECOVERY_CONFIRMED incumbentPid=$($LockOwner.Id) state=exited")
}

function Resolve-ConductStop {
    param(
        [string]$RepositoryRoot,
        [string]$StopFilePath,
        $Process
    )

    if (-not (Test-Path -LiteralPath $StopFilePath -PathType Leaf)) {
        return [pscustomobject]@{ BlocksCurrentProcess = $false }
    }

    $targetPid = 0
    try {
        $stopRecord = Get-Content -LiteralPath $StopFilePath -Raw | ConvertFrom-Json
        $targetProperty = $stopRecord.PSObject.Properties["targetPid"]
        if ($null -ne $targetProperty) {
            [void][int]::TryParse([string]$targetProperty.Value, [ref]$targetPid)
        }
    }
    catch {
        $targetPid = 0
    }

    if ($null -eq $Process) {
        if ($targetPid -gt 0) {
            Write-ResumeReceipt -RepositoryRoot $RepositoryRoot -Message (
                "RESUME_BLOCKED reason=conduct-stop-target-not-running targetPid=$targetPid path=$StopFilePath")
            throw "Targeted .conduct-stop remains in effect after conduct PID $targetPid exited; operator removal is required."
        }

        Write-ResumeReceipt -RepositoryRoot $RepositoryRoot -Message (
            "RESUME_BLOCKED reason=conduct-stop-target-unknown path=$StopFilePath")
        throw "Unscoped .conduct-stop cannot be safely attributed while no conduct process is running."
    }

    if ($targetPid -gt 0) {
        if ($targetPid -eq $Process.Id) {
            return [pscustomobject]@{ BlocksCurrentProcess = $true }
        }

        Remove-Item -LiteralPath $StopFilePath -Force
        Write-ResumeReceipt -RepositoryRoot $RepositoryRoot -Message (
            "RESUME_STOP_STALE targetPid=$targetPid activePid=$($Process.Id) reason=target-mismatch action=removed")
        return [pscustomobject]@{ BlocksCurrentProcess = $false }
    }

    $stopWrittenAt = [DateTimeOffset](Get-Item -LiteralPath $StopFilePath).LastWriteTimeUtc
    if ($null -ne $Process.CreatedAt -and $stopWrittenAt -lt $Process.CreatedAt) {
        Remove-Item -LiteralPath $StopFilePath -Force
        Write-ResumeReceipt -RepositoryRoot $RepositoryRoot -Message (
            "RESUME_STOP_STALE activePid=$($Process.Id) reason=older-than-process action=removed")
        return [pscustomobject]@{ BlocksCurrentProcess = $false }
    }

    [ordered]@{
        schemaVersion = 1
        targetPid = $Process.Id
        createdAt = $stopWrittenAt.ToString("O", [System.Globalization.CultureInfo]::InvariantCulture)
    } | ConvertTo-Json | Set-Content -LiteralPath $StopFilePath -Encoding UTF8
    Write-ResumeReceipt -RepositoryRoot $RepositoryRoot -Message (
        "RESUME_STOP_BOUND targetPid=$($Process.Id) path=$StopFilePath")
    return [pscustomobject]@{ BlocksCurrentProcess = $true }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$stopFilePath = Join-Path $repoRoot ".conduct-stop"
$resolvedJournalPath = Resolve-JournalPath -RepositoryRoot $repoRoot -ConfiguredPath $JournalPath
$journal = $null
if (Test-Path -LiteralPath $resolvedJournalPath -PathType Leaf) {
    $journal = Get-Content -LiteralPath $resolvedJournalPath -Raw | ConvertFrom-Json
}
$arguments = if ($null -eq $journal) {
    @()
}
else {
    @($journal.arguments | ForEach-Object { [string]$_ })
}
$journalIsConductLoop = $arguments.Count -ge 2 -and
    $arguments[0].Equals("conduct", [System.StringComparison]::OrdinalIgnoreCase) -and
    [bool]($arguments | Where-Object { $_.Equals("--loop", [System.StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1)

try {
    $processes = @(Get-ConductLoopProcesses -RepositoryRoot $repoRoot)
    $lockOwner = Get-ConductLockOwner -RepositoryRoot $repoRoot
    $conductProcess = $null
    if ($null -ne $lockOwner) {
        $matchingProcesses = @($processes | Where-Object { $_.Id -eq $lockOwner.Id } | Select-Object -First 1)
        if ($matchingProcesses.Count -gt 0) {
            $conductProcess = $matchingProcesses[0]
        }
    }
    elseif ($processes.Count -eq 1) {
        $conductProcess = $processes[0]
    }
    if ($processes.Count -gt 0 -and $null -eq $conductProcess) {
        throw "Conduct process identity is ambiguous: found $($processes.Count) candidate(s) without a matching conduct-loop lock owner."
    }

    $stopDisposition = Resolve-ConductStop `
        -RepositoryRoot $repoRoot `
        -StopFilePath $stopFilePath `
        -Process $conductProcess

    if ($null -ne $conductProcess) {
        $pollSeconds = if ($null -eq $journal) { 15 } else { Get-PollSeconds -Journal $journal }
        $liveness = Get-ConductLiveness `
            -RepositoryRoot $repoRoot `
            -Process $conductProcess `
            -PollSeconds $pollSeconds `
            -PollIntervals $StaleAfterPollIntervals
        if (-not $liveness.EvidenceAvailable) {
            Write-ResumeReceipt -RepositoryRoot $repoRoot -Message (
                "RESUME_BLOCKED reason=liveness-evidence-unavailable incumbentPid=$($conductProcess.Id) eventLog=$($liveness.EventLogPath)")
            exit 1
        }
        if ($liveness.IsHealthy) {
            $ageSeconds = [Math]::Floor($liveness.Age.TotalSeconds)
            Write-Output "RESUME_SKIPPED reason=conduct-loop-healthy incumbentPid=$($conductProcess.Id) eventAgeSeconds=$ageSeconds"
            exit 0
        }
        if (-not $journalIsConductLoop) {
            Write-ResumeReceipt -RepositoryRoot $repoRoot -Message (
                "RESUME_BLOCKED reason=journal-unavailable-for-recovery incumbentPid=$($conductProcess.Id) path=$resolvedJournalPath")
            exit 1
        }

        Stop-ConfirmedConductProcess `
            -RepositoryRoot $repoRoot `
            -Process $conductProcess `
            -LockOwner $lockOwner `
            -Liveness $liveness
        if ($stopDisposition.BlocksCurrentProcess) {
            Write-ResumeReceipt -RepositoryRoot $repoRoot -Message (
                "RESUME_SKIPPED reason=conduct-stop-fulfilled targetPid=$($conductProcess.Id)")
            exit 0
        }
    }
}
catch {
    Write-Output "RESUME_FAILED reason=recovery-unavailable detail=$($_.Exception.Message)"
    exit 1
}

if ($null -eq $journal) {
    Write-Output "RESUME_SKIPPED reason=journal-missing path=$resolvedJournalPath"
    exit 0
}

if ($arguments.Count -lt 2 -or
    -not $arguments[0].Equals("conduct", [System.StringComparison]::OrdinalIgnoreCase) -or
    -not ($arguments | Where-Object { $_.Equals("--loop", [System.StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1)) {
    Write-Output "RESUME_SKIPPED reason=journal-not-conduct-loop path=$resolvedJournalPath"
    exit 0
}

$nextName = Resolve-NextBatchName -CurrentName ([string]$journal.name)
$startScriptPath = Join-Path $repoRoot "scripts\Start-OrchestratorCommand.ps1"
if ($null -ne $journal.appDll -and -not [string]::IsNullOrWhiteSpace([string]$journal.appDll)) {
    Write-Output "RESUME_LAUNCH task=$TaskName journal=$resolvedJournalPath name=$nextName"
    & $startScriptPath -Name $nextName -AppDll ([string]$journal.appDll) @arguments
}
else {
    Write-Output "RESUME_LAUNCH task=$TaskName journal=$resolvedJournalPath name=$nextName"
    & $startScriptPath -Name $nextName @arguments
}
if ($LASTEXITCODE -is [int] -and $LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
